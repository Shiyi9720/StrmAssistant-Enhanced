using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Rewrites OfficialRating in the property setter, before Emby persists metadata. The mapping
    /// table lives in its own JSON file so it can grow without expanding PluginOptions.
    /// </summary>
    internal static class UnifiedRatingService
    {
        private const string MapFileName = "StrmAssistant.UnifiedRatings.json";
        private const string DefaultTarget = "US:TV-MA";

        private static readonly object Sync = new object();
        private static IJsonSerializer _jsonSerializer;
        private static Harmony _harmony;
        private static bool _patchFailed;
        private static RatingMapCache _cache;

        public static void Initialize(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer;

            if (_harmony != null || _patchFailed) return;

            try
            {
                var setter = typeof(BaseItem).GetProperty(nameof(BaseItem.OfficialRating))?.GetSetMethod(true);

                if (setter is null)
                {
                    _patchFailed = true;
                    return;
                }

                _harmony = new Harmony("StrmAssistant.UnifiedRating");
                _harmony.Patch(setter, prefix: new HarmonyMethod(
                    typeof(UnifiedRatingService).GetMethod(nameof(OfficialRatingPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                _patchFailed = true;
                Plugin.Instance?.Logger?.Debug("Unified rating hook failed: " + e.Message);
            }
        }

        public static void ApplyOptions(PluginOptions options)
        {
            if (options is null || options.UnifiedRatingMode == UnifiedRatingMode.Off) return;

            try
            {
                var path = GetMapPath();

                if (_jsonSerializer is null || File.Exists(path)) return;

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                _jsonSerializer.SerializeToFile(new RatingMapDocument
                {
                    mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["TV-MA"] = string.Empty,
                        ["R"] = string.Empty,
                        ["NC-17"] = string.Empty,
                        ["18"] = string.Empty
                    }
                }, path);
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Unified rating map init failed: " + e.Message);
            }
        }

        private static void OfficialRatingPrefix(BaseItem __instance, ref string value)
        {
            try
            {
                var options = Plugin.Instance?.GetPluginOptions();

                if (options is null || options.UnifiedRatingMode == UnifiedRatingMode.Off ||
                    string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                if (!TryMap(value, options, out var target)) return;

                if (options.UnifiedRatingMode == UnifiedRatingMode.RecordOnly)
                {
                    Plugin.Instance?.Logger?.Debug(
                        "Unified rating (record only) - {0}: {1} -> {2}",
                        __instance?.Name, value, target);
                    return;
                }

                if (!string.Equals(value, target, StringComparison.Ordinal))
                {
                    value = target;
                }
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Unified rating mapping failed: " + e.Message);
            }
        }

        private static bool TryMap(string source, PluginOptions options, out string target)
        {
            target = null;

            var mappings = GetMappings();

            if (!mappings.TryGetValue(source.Trim(), out var mapped)) return false;

            target = string.IsNullOrWhiteSpace(mapped)
                ? string.IsNullOrWhiteSpace(options.UnifiedRatingTarget)
                    ? DefaultTarget
                    : options.UnifiedRatingTarget.Trim()
                : mapped.Trim();

            return !string.IsNullOrWhiteSpace(target);
        }

        private static Dictionary<string, string> GetMappings()
        {
            var path = GetMapPath();

            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var info = new FileInfo(path);
            var stamp = info.LastWriteTimeUtc.Ticks + ":" + info.Length;

            lock (Sync)
            {
                if (_cache != null && _cache.Stamp == stamp) return _cache.Mappings;

                try
                {
                    var document = _jsonSerializer.DeserializeFromFile<RatingMapDocument>(path);
                    var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var pair in document?.mappings ??
                             new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key))
                        {
                            mappings[pair.Key.Trim()] = pair.Value ?? string.Empty;
                        }
                    }

                    _cache = new RatingMapCache { Stamp = stamp, Mappings = mappings };
                    return mappings;
                }
                catch (Exception e)
                {
                    Plugin.Instance?.Logger?.Debug("Unified rating map read failed: " + e.Message);
                    _cache = new RatingMapCache
                    {
                        Stamp = stamp,
                        Mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    };
                    return _cache.Mappings;
                }
            }
        }

        private static string GetMapPath()
        {
            return Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath, MapFileName);
        }

        private sealed class RatingMapCache
        {
            public string Stamp { get; set; }
            public Dictionary<string, string> Mappings { get; set; }
        }

        private sealed class RatingMapDocument
        {
            public Dictionary<string, string> mappings { get; set; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
