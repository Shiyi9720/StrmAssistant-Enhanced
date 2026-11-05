using Emby.Media.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Excludes videos from chapter image and BIF extraction. Videos carrying the
    /// <c>videothumbnail-exclude</c> tag, matching the configured keywords, or using an
    /// excluded media container are skipped.
    /// </summary>
    public sealed class VideoThumbnailBlacklist : ItemBlacklist
    {
        public const string ExcludeTagName = "videothumbnail-exclude";

        private readonly HashSet<MediaContainers> _containers;
        private readonly HashSet<string> _containerAliases;

        private VideoThumbnailBlacklist(string keywords, HashSet<MediaContainers> containers,
            HashSet<string> containerAliases) : base(ExcludeTagName, keywords)
        {
            _containers = containers;
            _containerAliases = containerAliases;
        }

        public static VideoThumbnailBlacklist FromOptions()
        {
            var options = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions;
            var containers = ParseContainers(options.VideoThumbnailExcludeMediaContainers);

            return new VideoThumbnailBlacklist(options.VideoThumbnailExcludeKeywords, containers,
                GetContainerAliases(containers));
        }

        public bool IsExcluded(Video item)
        {
            if (item is null) return false;

            return base.IsExcluded(item) || IsContainerExcluded(item);
        }

        private bool IsContainerExcluded(BaseItem item)
        {
            if (_containers.Count == 0) return false;

            if (item.MediaContainer.HasValue && _containers.Contains(item.MediaContainer.Value)) return true;

            var extension = Path.GetExtension(item.Path)?.TrimStart('.');

            return !string.IsNullOrEmpty(extension) && _containerAliases.Contains(extension);
        }

        private static HashSet<MediaContainers> ParseContainers(string value)
        {
            var containers = new HashSet<MediaContainers>();

            if (string.IsNullOrWhiteSpace(value)) return containers;

            foreach (var token in value.Split(new[] { ',', ';', '\r', '\n', '|' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var name = token.Trim();

                if (name.Length == 0 || int.TryParse(name, out _)) continue;

                if (Enum.TryParse<MediaContainers>(name, true, out var container))
                {
                    containers.Add(container);
                }
            }

            return containers;
        }

        private static HashSet<string> GetContainerAliases(IEnumerable<MediaContainers> containers)
        {
            return new HashSet<string>(containers.SelectMany(c => c.GetAliases() ?? Array.Empty<string>())
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim().TrimStart('.')), StringComparer.OrdinalIgnoreCase);
        }
    }
}
