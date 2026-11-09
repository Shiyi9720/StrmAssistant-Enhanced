using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using StrmAssistant.Provider;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// Fills episode titles that were never populated by a metadata provider. Only episodes
    /// whose title is empty, identical to the file name, a pure number or one of Emby's default
    /// names are touched; an existing title (including a locked one) is never overwritten.
    /// The TMDB episode list is read through the plugin's cache, so a repeated run normally
    /// performs no HTTP requests.
    /// </summary>
    public class BeautifyMissingMetadataTask : IScheduledTask, IConfigurableScheduledTask
    {
        private const int BatchSize = 200;
        private const int BatchDelayMilliseconds = 100;

        private readonly ILibraryManager _libraryManager;
        private ILogger _logger => Plugin.Instance?.Logger;

        public BeautifyMissingMetadataTask(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.Instance.GetPluginOptions().ExperienceEnhanceOptions;

            if (!options.BeautifyMissingMetadata)
            {
                _logger?.Info("BeautifyMissingMetadata - Disabled, skipped");
                progress?.Report(100);
                return;
            }

            _logger?.Info("BeautifyMissingMetadata - Scheduled Task Execute");
            progress?.Report(0);

            var scope = options.BeautifyMissingMetadataLibraryScope
                ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(l => !LibraryApi.ExcludedCollectionTypes.Contains(l.CollectionType))
                .Where(l => string.Equals(l.CollectionType, CollectionType.TvShows.ToString(),
                                StringComparison.OrdinalIgnoreCase) || l.CollectionType is null)
                .Where(l => scope is null || scope.Count == 0 ||
                            scope.Contains(l.ItemId) || scope.Contains(l.Id) || scope.Contains(l.Guid))
                .ToList();

            var scopeDescription = scope is null || scope.Count == 0
                ? "ALL"
                : libraries.Any()
                    ? string.Join(", ", libraries.Select(l => l.Name))
                    : "NONE";
            _logger?.Info("BeautifyMissingMetadata - LibraryScope: " + scopeDescription);

            var seriesIds = new List<long>();

            foreach (var library in libraries)
            {
                BaseItem libraryFolder = null;

                if (Guid.TryParse(library.Guid, out var libraryGuid))
                {
                    libraryFolder = _libraryManager.GetItemById(libraryGuid);
                }

                if (libraryFolder is null && !string.IsNullOrEmpty(library.ItemId))
                {
                    libraryFolder = _libraryManager.GetItemById(library.ItemId);
                }

                if (libraryFolder is null)
                {
                    _logger?.Warn("BeautifyMissingMetadata - Library not found: " + library.Name);
                    continue;
                }

                var ids = _libraryManager.GetInternalItemIds(new InternalItemsQuery
                {
                    AncestorIds = new[] { libraryFolder.InternalId },
                    Recursive = true,
                    IncludeItemTypes = new[] { nameof(Series) }
                });

                _logger?.Info($"BeautifyMissingMetadata - {library.Name}: {ids.Length} series");
                seriesIds.AddRange(ids);
            }

            seriesIds = seriesIds.Distinct().ToList();
            var total = seriesIds.Count;

            if (total == 0)
            {
                _logger?.Info("BeautifyMissingMetadata - No series in scope");
                progress?.Report(100);
                return;
            }

            var processed = 0;
            var filled = 0;
            var checkedEpisodes = 0;
            var skippedLocked = 0;
            var skippedExisting = 0;
            var skippedNoTmdb = 0;
            var skippedNoMatch = 0;
            var failed = 0;

            for (var offset = 0; offset < seriesIds.Count; offset += BatchSize)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger?.Info("BeautifyMissingMetadata - Scheduled Task Cancelled");
                    return;
                }

                var batchIds = seriesIds.Skip(offset).Take(BatchSize).ToArray();
                var seriesBatch = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = batchIds })
                    .OfType<Series>()
                    .ToList();

                foreach (var series in seriesBatch)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger?.Info("BeautifyMissingMetadata - Scheduled Task Cancelled");
                        return;
                    }

                    processed++;

                    try
                    {
                        var tmdbId = series.GetProviderId(MetadataProviders.Tmdb);

                        if (string.IsNullOrEmpty(tmdbId))
                        {
                            skippedNoTmdb++;
                            continue;
                        }

                        var language = Plugin.MetadataApi.GetPreferredMetadataLanguage(series);
                        var seriesResponse = await Plugin.MetadataApi
                            .GetSeriesAllEpisodes(tmdbId, language, cancellationToken)
                            .ConfigureAwait(false);

                        if (seriesResponse?.seasons is null)
                        {
                            failed++;
                            _logger?.Info("BeautifyMissingMetadata - No TMDB episodes: " + series.Name);
                            continue;
                        }

                        var tmdbEpisodes = new Dictionary<(int season, int episode), EpisodeResponseInfo>();

                        foreach (var season in seriesResponse.seasons)
                        {
                            if (season?.episodes is null) continue;

                            foreach (var episode in season.episodes)
                            {
                                if (episode is null) continue;

                                tmdbEpisodes[(season.season_number, episode.episode_number)] = episode;
                            }
                        }

                        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            AncestorIds = new[] { series.InternalId },
                            Recursive = true,
                            IncludeItemTypes = new[] { nameof(Episode) }
                        }).OfType<Episode>();

                        var changed = new List<BaseItem>();

                        foreach (var episode in episodes)
                        {
                            checkedEpisodes++;

                            if (!NeedsBeautify(episode))
                            {
                                skippedExisting++;
                                continue;
                            }

                            if (episode.IsFieldLocked(MetadataFields.Name))
                            {
                                skippedLocked++;
                                continue;
                            }

                            if (!episode.IndexNumber.HasValue)
                            {
                                skippedNoMatch++;
                                continue;
                            }

                            var localSeason = episode.ParentIndexNumber ?? 0;
                            var localEpisode = episode.IndexNumber.Value;
                            var tmdbSeason = localSeason;
                            var tmdbEpisode = localEpisode;

                            if (LocalEpisodeGroup.TryGetTmdbNumbers(tmdbId, localSeason, localEpisode,
                                    out var mappedSeason, out var mappedEpisode) && mappedEpisode.HasValue)
                            {
                                tmdbSeason = mappedSeason;
                                tmdbEpisode = mappedEpisode.Value;
                            }

                            if (!tmdbEpisodes.TryGetValue((tmdbSeason, tmdbEpisode), out var tmdbEpisodeInfo) ||
                                string.IsNullOrWhiteSpace(tmdbEpisodeInfo.name))
                            {
                                skippedNoMatch++;
                                continue;
                            }

                            episode.Name = tmdbEpisodeInfo.name.Trim();
                            changed.Add(episode);
                        }

                        if (changed.Count > 0)
                        {
                            _libraryManager.UpdateItems(changed, null, ItemUpdateType.MetadataEdit, false,
                                false, null, cancellationToken);
                            filled += changed.Count;
                            _logger?.Info($"BeautifyMissingMetadata - {series.Name}: filled {changed.Count} title(s)");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger?.Info("BeautifyMissingMetadata - Scheduled Task Cancelled");
                        return;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        _logger?.Error("BeautifyMissingMetadata - Series failed: " + series.Name);
                        _logger?.Error(e.Message);
                        _logger?.Debug(e.StackTrace);
                    }

                    progress?.Report(Math.Min(100.0, processed * 100.0 / total));
                }

                if (offset + BatchSize < seriesIds.Count)
                {
                    await Task.Delay(BatchDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }

            _logger?.Info("BeautifyMissingMetadata - Scheduled Task Complete");
            _logger?.Info($"BeautifyMissingMetadata - Filled: {filled}, Series: {processed}, " +
                          $"Episodes: {checkedEpisodes}, Existing: {skippedExisting}, Locked: {skippedLocked}, " +
                          $"NoTmdbId: {skippedNoTmdb}, NoMatch: {skippedNoMatch}, Failed: {failed}");

            progress?.Report(100.0);
        }

        /// <summary>
        /// True when the current title carries no real metadata: it is empty, equal to the file
        /// name, a pure number, or one of Emby's generated "Episode N" style names.
        /// </summary>
        private static bool NeedsBeautify(Episode episode)
        {
            var name = episode.Name;

            if (string.IsNullOrWhiteSpace(name)) return true;

            var fileName = episode.FileNameWithoutExtension;

            if (!string.IsNullOrWhiteSpace(fileName) &&
                string.Equals(name, fileName, StringComparison.Ordinal))
            {
                return true;
            }

            var trimmed = name.Trim();

            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return true;

            return IsDefaultChineseEpisodeName(trimmed) ||
                   IsDefaultEnglishEpisodeName(trimmed) ||
                   IsDefaultJapaneseEpisodeName(trimmed);
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "BeautifyMissingMetadataTask";

        public string Name => Resources.ResourceManager.GetString(
            "UIFunctionOptions_BeautifyMissingMetadata_Beautify_Missing_Metadata",
            Plugin.Instance.DefaultUICulture);

        public string Description => Resources.ResourceManager.GetString(
            "UIFunctionOptions_BeautifyMissingMetadata_Beautify_missing_metadata_for_episode_display__Default_is_OFF_",
            Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        private static bool IsOptionEnabled =>
            Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions?.BeautifyMissingMetadata == true;

        public bool IsHidden => !IsOptionEnabled;

        public bool IsEnabled => IsOptionEnabled;

        public bool IsLogged => true;
    }
}
