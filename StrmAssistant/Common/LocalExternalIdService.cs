using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Concurrent;
using System.IO;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Applies external_ids.json to an item's provider ids. Gated by the independent
    /// LocalExternalIdsEnabled switch (default off) and requires the local metadata source.
    /// </summary>
    internal static class LocalExternalIdService
    {
        private static readonly ConcurrentDictionary<long, byte> InFlight =
            new ConcurrentDictionary<long, byte>();

        private static ILibraryManager _libraryManager;

        public static void Initialize(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public static void Apply(BaseItem item)
        {
            if (item is null || _libraryManager is null) return;

            if (!InFlight.TryAdd(item.InternalId, 0)) return;

            try
            {
                var options = Plugin.Instance?.GetPluginOptions();

                if (options is null || !options.EnableLocalMetadataSource ||
                    !options.LocalExternalIdsEnabled)
                {
                    return;
                }

                var root = LocalMetadataFiles.NormalizeRoot(options.LocalMetadataRoot);
                var tmdbId = GetTmdbId(item);
                var kind = GetKind(item);

                if (root is null || tmdbId is null || !kind.HasValue) return;

                var path = LocalMetadataFiles.FindFile(root, kind.Value, tmdbId,
                    LocalMetadataFiles.ExternalIdsFileName);

                if (path is null) return;

                var ids = LocalMetadataItemRules.ReadExternalIds(File.ReadAllBytes(path));

                if (ids is null || ids.Count == 0) return;
                if (item.ProviderIds is null) return;

                var changed = false;

                foreach (var pair in ids)
                {
                    if (item.ProviderIds.TryGetValue(pair.Key, out var existing) &&
                        string.Equals(existing, pair.Value, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    item.ProviderIds[pair.Key] = pair.Value;
                    changed = true;
                }

                if (!changed) return;

                _libraryManager.UpdateItem(item, item.Parent, ItemUpdateType.MetadataEdit);
                Plugin.Instance?.Logger?.Info("Local metadata external ids applied - {0} ({1})",
                    item.Name, path);
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Local metadata external ids failed - " +
                                               item?.Name + ": " + e.Message);
            }
            finally
            {
                InFlight.TryRemove(item.InternalId, out _);
            }
        }

        internal static LocalMetadataFileKind? GetKind(BaseItem item)
        {
            if (item is Movie) return LocalMetadataFileKind.Movie;
            if (item is Series) return LocalMetadataFileKind.Series;
            if (item is Season) return LocalMetadataFileKind.Season;
            if (item is Episode) return LocalMetadataFileKind.Episode;
            if (item is BoxSet) return LocalMetadataFileKind.Collection;

            return null;
        }

        internal static string GetTmdbId(BaseItem item)
        {
            try
            {
                if (item is Season season)
                {
                    if (TryGet(season, out var seasonId)) return seasonId;
                    return season.Series != null && TryGet(season.Series, out var seriesId)
                        ? seriesId
                        : null;
                }

                if (item is Episode episode)
                {
                    if (TryGet(episode, out var episodeId)) return episodeId;
                    return episode.Series != null && TryGet(episode.Series, out var seriesId)
                        ? seriesId
                        : null;
                }

                return TryGet(item, out var id) ? id : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool TryGet(BaseItem item, out string id)
        {
            id = null;

            return item?.ProviderIds != null &&
                   item.ProviderIds.TryGetValue("Tmdb", out id) &&
                   !string.IsNullOrWhiteSpace(id);
        }
    }
}
