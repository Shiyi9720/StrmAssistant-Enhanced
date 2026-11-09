using Emby.Naming.Common;
using HarmonyLib;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// Beautifies the display of episodes that have no metadata: an episode whose title is
    /// still the file name is shown as "第 N 集" for Chinese metadata, and additional parts are
    /// renamed to 下部分/第 N 部分. The naming expression is also trimmed so that a file name
    /// consisting only of a season/episode number is not treated as the episode title.
    /// Persisting the real TMDB titles is done by <see cref="ScheduledTask.BeautifyMissingMetadataTask"/>.
    /// </summary>
    public class BeautifyMissingMetadata : PatchBase<BeautifyMissingMetadata>
    {
        private static MethodInfo _getBaseItemDtos;
        private static MethodInfo _getBaseItemDto;
        private static MethodInfo _getMainExpression;

        private const string SeasonNumberAndEpisodeNumberExpression =
            "(?<![a-z]|[0-9])(?<seasonnumber>[0-9]+)(?:[ ._x-]*e|x|[ ._-]*ep[._ -]*|[ ._-]*episode[._ -]+)";

        public BeautifyMissingMetadata()
        {
            Initialize();

            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony)
            {
                return;
            }

            Patch();
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var dtoService = embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Dto.DtoService");

            _getBaseItemDtos = dtoService?
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "GetBaseItemDtos")
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();

            // The three-parameter overload only delegates to the four-parameter one; patching the
            // latter covers both call paths.
            _getBaseItemDto = dtoService?.GetMethod("GetBaseItemDto", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(BaseItem), typeof(DtoOptions), typeof(User), typeof(CancellationToken) },
                null);

            _getMainExpression =
                typeof(NamingOptions).GetMethod("GetMainExpression", BindingFlags.NonPublic | BindingFlags.Static);

            if (_getBaseItemDtos is null || _getBaseItemDto is null || _getMainExpression is null)
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.FailureReason =
                    (_getBaseItemDtos is null ? "GetBaseItemDtos" :
                        _getBaseItemDto is null ? "GetBaseItemDto" : "GetMainExpression") +
                    " could not be located";

                Plugin.Instance.Logger.Warn(
                    "BeautifyMissingMetadata - The Emby methods to patch could not be located");
            }
        }

        protected override void Prepare(bool apply)
        {
            var specs = new[]
            {
                new PatchSpec("GetBaseItemDtos", _getBaseItemDtos, postfix: nameof(GetBaseItemDtosPostfix)),
                new PatchSpec("GetBaseItemDto", _getBaseItemDto, postfix: nameof(GetBaseItemDtoPostfix)),
                new PatchSpec("GetMainExpression", _getMainExpression, postfix: nameof(GetMainExpressionPostfix))
            };

            if (apply)
            {
                PatchAll(PatchTracker, specs);
                return;
            }

            foreach (var spec in specs)
            {
                if (spec.Target is null) continue;

                PatchUnpatch(PatchTracker, false, spec.Target, spec.Prefix, spec.Postfix, spec.Transpiler,
                    spec.Finalizer, true);
            }
        }

        [HarmonyPostfix]
        private static void GetBaseItemDtosPostfix(BaseItem[] items, ref BaseItemDto[] __result)
        {
            if (items is null || items.Length == 0 || __result is null) return;

            var checkItem = items.FirstOrDefault();

            if (checkItem is null || !string.Equals(checkItem.GetPreferredMetadataLanguage(), "zh-CN",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (checkItem.ExtraType == ExtraType.AdditionalPart)
            {
                var videos = __result.Where(i => i.Type == nameof(Video)).ToList();
                var videoCount = videos.Count;

                foreach (var (currentItem, index) in videos.Select((currentItem, index) => (currentItem, index)))
                {
                    if (videoCount == 1)
                    {
                        currentItem.Name = "下部分";
                        return;
                    }

                    currentItem.Name = $"第{index + 2}部分";
                }

                return;
            }

            if (checkItem is Episode)
            {
                var episodes = !string.IsNullOrEmpty(checkItem.FileNameWithoutExtension)
                    ? items
                    : Plugin.LibraryApi.GetItemsByIds(items.Select(i => i.InternalId).ToArray());

                // Map by internal id so a re-fetched or reordered list can never rename the
                // wrong DTO.
                var dtosById = new Dictionary<long, BaseItemDto>();

                for (var i = 0; i < items.Length && i < __result.Length; i++)
                {
                    if (items[i] != null && items[i].InternalId != 0)
                    {
                        dtosById[items[i].InternalId] = __result[i];
                    }
                }

                foreach (var currentItem in episodes)
                {
                    if (currentItem != null && currentItem.IndexNumber.HasValue &&
                        string.Equals(currentItem.Name, currentItem.FileNameWithoutExtension,
                            StringComparison.Ordinal) &&
                        dtosById.TryGetValue(currentItem.InternalId, out var dto))
                    {
                        dto.Name = $"第 {currentItem.IndexNumber} 集";
                    }
                }
            }
        }

        [HarmonyPostfix]
        private static void GetBaseItemDtoPostfix(BaseItem item, ref BaseItemDto __result)
        {
            if (__result is null || !(item is Episode) || !item.IndexNumber.HasValue) return;

            if (!string.Equals(item.GetPreferredMetadataLanguage(), "zh-CN", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(item.Name, item.FileNameWithoutExtension, StringComparison.Ordinal))
            {
                __result.Name = $"第 {item.IndexNumber} 集";
            }
        }

        [HarmonyPostfix]
        private static void GetMainExpressionPostfix(ref string __result, bool allowEpisodeNumberOnly,
            bool allowMultiEpisodeNumberOnlyExpression, bool allowX)
        {
            if (string.IsNullOrEmpty(__result)) return;

            if (allowEpisodeNumberOnly && !allowMultiEpisodeNumberOnlyExpression && allowX)
            {
                __result = Regex.Replace(__result,
                    Regex.Escape(SeasonNumberAndEpisodeNumberExpression) + @"\|?", string.Empty);
            }
        }
    }
}
