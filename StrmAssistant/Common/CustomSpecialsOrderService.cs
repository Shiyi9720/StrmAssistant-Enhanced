using MediaBrowser.Controller.Entities;
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
    /// Applies a custom air order to season 0 episodes. Emby sorts specials with
    /// SortParentIndexNumber/SortIndexNumber, so both fields are kept in season 0 while the
    /// index number carries the configured air order.
    /// </summary>
    internal static class CustomSpecialsOrderService
    {
        private const string FileName = "specials.json";
        private const string OverrideFolderName = "override";
        private const string CacheFolderName = "cache";
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
            if (!(item is Episode episode) || _libraryManager is null || _jsonSerializer is null) return;

            if (!InFlight.TryAdd(episode.InternalId, 0)) return;

            try
            {
                var options = Plugin.Instance?.GetPluginOptions();

                if (options is null || !options.EnableCustomSpecialsOrder) return;
                if (episode.ParentIndexNumber != 0 && episode.Season?.IndexNumber != 0) return;
                if (!episode.IndexNumber.HasValue) return;

                var path = FindFile(episode);

                if (path is null) return;

                var document = _jsonSerializer.DeserializeFromFile<SpecialsDocument>(path);
                var entry = document?.episodes?
                    .Where(value => value != null && value.episode_number == episode.IndexNumber.Value)
                    .FirstOrDefault();
                var airOrder = GetAirOrder(entry);

                if (!airOrder.HasValue) return;
                if (episode.SortParentIndexNumber == 0 && episode.SortIndexNumber == airOrder.Value) return;

                episode.SortParentIndexNumber = 0;
                episode.SortIndexNumber = airOrder.Value;
                _libraryManager.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit);

                Plugin.Instance?.Logger?.Debug("Custom special order applied - {0}: S{1}E{2} -> {3}",
                    episode.Name, episode.ParentIndexNumber, episode.IndexNumber, airOrder.Value);
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Custom special order failed - " + item.Name + ": " + e.Message);
            }
            finally
            {
                InFlight.TryRemove(episode.InternalId, out _);
            }
        }

        private static int? GetAirOrder(SpecialsEpisode entry)
        {
            if (entry is null) return null;

            if (entry.air_order.HasValue) return entry.air_order;
            if (entry.airOrder.HasValue) return entry.airOrder;
            if (entry.sort_index_number.HasValue) return entry.sort_index_number;
            if (entry.sortIndexNumber.HasValue) return entry.sortIndexNumber;

            return entry.order;
        }

        private static string FindFile(Episode episode)
        {
            var series = episode.Series;
            var tmdbId = series != null && series.ProviderIds.TryGetValue("Tmdb", out var id) ? id : null;
            var root = NormalizeRoot(Plugin.Instance?.GetPluginOptions()?.LocalMetadataRoot);

            if (root != null && !string.IsNullOrWhiteSpace(tmdbId))
            {
                foreach (var candidate in new[]
                         {
                             Path.Combine(root, OverrideFolderName, TvFolderName, tmdbId, FileName),
                             Path.Combine(root, CacheFolderName, TvFolderName, tmdbId, FileName),
                             Path.Combine(root, OverrideFolderName, tmdbId, FileName)
                         })
                {
                    if (File.Exists(candidate)) return candidate;
                }
            }

            foreach (var directory in new[]
                     {
                         series?.ContainingFolderPath,
                         series?.Path,
                         episode.ContainingFolderPath
                     }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(directory, FileName);

                if (File.Exists(candidate)) return candidate;
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

        private sealed class SpecialsDocument
        {
            public List<SpecialsEpisode> episodes { get; set; }
        }

        private sealed class SpecialsEpisode
        {
            public int episode_number { get; set; }
            public int? air_order { get; set; }
            public int? airOrder { get; set; }
            public int? sort_index_number { get; set; }
            public int? sortIndexNumber { get; set; }
            public int? order { get; set; }
        }
    }
}
