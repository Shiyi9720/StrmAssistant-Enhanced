using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Concurrent;
using System.IO;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Applies episodeorder.json (and the legacy specials.json) to an episode's sort order.
    /// SortParentIndexNumber keeps the library season; SortIndexNumber carries the configured
    /// air order. Gated by the independent LocalEpisodeOrderEnabled switch (default off) and
    /// requires the local metadata source.
    /// </summary>
    internal static class LocalEpisodeOrderService
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
            if (!(item is Episode episode) || _libraryManager is null) return;

            if (!InFlight.TryAdd(episode.InternalId, 0)) return;

            try
            {
                var options = Plugin.Instance?.GetPluginOptions();

                if (options is null || !options.EnableLocalMetadataSource ||
                    !options.LocalEpisodeOrderEnabled)
                {
                    return;
                }

                var root = LocalMetadataFiles.NormalizeRoot(options.LocalMetadataRoot);
                var tmdbId = LocalExternalIdService.GetTmdbId(episode);

                if (root is null || tmdbId is null) return;

                var path = LocalMetadataFiles.FindFile(root, LocalMetadataFileKind.Episode, tmdbId,
                               LocalMetadataFiles.EpisodeOrderFileName) ??
                           LocalMetadataFiles.FindFile(root, LocalMetadataFileKind.Episode, tmdbId,
                               LocalMetadataFiles.LegacySpecialsFileName);

                if (path is null) return;

                var entries = LocalMetadataItemRules.ReadEpisodeOrder(File.ReadAllBytes(path));

                if (entries is null || entries.Count == 0) return;

                var season = episode.ParentIndexNumber ?? 0;
                var airOrder = LocalMetadataItemRules.TryResolveAirOrder(entries, season,
                    episode.IndexNumber);

                if (!airOrder.HasValue) return;
                if (episode.SortParentIndexNumber == season && episode.SortIndexNumber == airOrder)
                    return;

                episode.SortParentIndexNumber = season;
                episode.SortIndexNumber = airOrder;
                _libraryManager.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit);

                Plugin.Instance?.Logger?.Info(
                    "Local episode order applied - {0}: S{1}E{2} -> {3} ({4})",
                    episode.Name, season, episode.IndexNumber, airOrder, path);
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Local episode order failed - " +
                                               item?.Name + ": " + e.Message);
            }
            finally
            {
                InFlight.TryRemove(episode.InternalId, out _);
            }
        }
    }
}
