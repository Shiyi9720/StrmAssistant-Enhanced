using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Reads tags.json from the local metadata override or passive cache and merges the
    /// configured tags into movie and series items. TMDB tags already on the item are kept.
    /// </summary>
    internal static class LocalMetadataTagService
    {
        private const string FileName = "tags.json";
        private const string OverrideFolderName = "override";
        private const string CacheFolderName = "cache";
        private const string MoviesFolderName = "tmdb-movies2";
        private const string TvFolderName = "tmdb-tv";

        private static readonly ConcurrentDictionary<long, byte> InFlight =
            new ConcurrentDictionary<long, byte>();

        private static ILibraryManager _libraryManager;
        private static IJsonSerializer _jsonSerializer;

        public static void Initialize(ILibraryManager libraryManager, IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _jsonSerializer = jsonSerializer;
        }

        public static void Apply(BaseItem item)
        {
            if (!(item is Movie) && !(item is Series)) return;
            if (_libraryManager is null || _jsonSerializer is null) return;

            if (!InFlight.TryAdd(item.InternalId, 0)) return;

            try
            {
                var options = Plugin.Instance?.GetPluginOptions();

                if (options is null || !options.EnableLocalMetadataSource) return;

                var root = NormalizeRoot(options.LocalMetadataRoot);

                if (root is null) return;

                var path = FindFile(root, item);

                if (path is null) return;

                var document = _jsonSerializer.DeserializeFromFile<TagsDocument>(path);
                var customTags = document?.tags?
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag.Trim())
                    .ToList();

                if (customTags is null || customTags.Count == 0) return;

                var existing = item.Tags ?? Array.Empty<string>();
                var merged = existing
                    .Concat(customTags)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (existing.SequenceEqual(merged, StringComparer.Ordinal)) return;

                item.SetTags(merged);
                _libraryManager.UpdateItem(item, item.Parent, ItemUpdateType.MetadataEdit);

                Plugin.Instance?.Logger?.Debug("Local metadata tags applied - {0} ({1}): {2}",
                    item.Name, path, string.Join(", ", customTags));
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Local metadata tags failed - " + item.Name + ": " + e.Message);
            }
            finally
            {
                InFlight.TryRemove(item.InternalId, out _);
            }
        }

        private static string FindFile(string root, BaseItem item)
        {
            var tmdbId = item.ProviderIds != null && item.ProviderIds.TryGetValue("Tmdb", out var id) ? id : null;

            if (string.IsNullOrWhiteSpace(tmdbId)) return null;

            var typeFolder = item is Movie ? MoviesFolderName : TvFolderName;
            var relativePaths = new[]
            {
                Path.Combine(typeFolder, tmdbId, FileName),
                Path.Combine(tmdbId, FileName)
            };

            foreach (var baseFolder in new[] { OverrideFolderName, CacheFolderName })
            {
                foreach (var relativePath in relativePaths)
                {
                    var candidate = Path.Combine(root, baseFolder, relativePath);

                    if (File.Exists(candidate)) return candidate;
                }
            }

            return null;
        }

        private static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;

            try
            {
                return Path.GetFullPath(root.Trim().Trim('"'));
            }
            catch
            {
                return null;
            }
        }

        private sealed class TagsDocument
        {
            public List<string> tags { get; set; }
        }
    }
}
