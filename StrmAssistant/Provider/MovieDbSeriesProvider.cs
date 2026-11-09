using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Provider
{
    /// <summary>
    /// Replacement for the built-in MovieDb series provider. Emby builds the "view missing
    /// episodes" list by asking the first enabled ISeriesMetadataProvider for the complete
    /// episode list and subtracting the episodes that exist in the library. This provider
    /// supplies that list through the plugin's mirror-aware TMDB client, including the local
    /// episode group mapping, so the view also works for libraries with custom season order.
    /// </summary>
    public class MovieDbSeriesProvider : ISeriesMetadataProvider
    {
        private const string ProviderName = "TheMovieDb";

        public string Name => ProviderName;

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            // Episode-list lookups never request images; the built-in providers handle those.
            return Task.FromResult<HttpResponseInfo>(null);
        }

        public async Task<RemoteSearchResult[]> GetAllEpisodes(SeriesInfo seriesInfo,
            CancellationToken cancellationToken)
        {
            if (!Plugin.Instance.GetPluginOptions().ExperienceEnhanceOptions.EnhanceMissingEpisodes)
            {
                return Array.Empty<RemoteSearchResult>();
            }

            var tmdbId = seriesInfo.GetProviderId(MetadataProviders.Tmdb);

            if (string.IsNullOrEmpty(tmdbId)) return Array.Empty<RemoteSearchResult>();

            var seriesResponse = await Plugin.MetadataApi
                .GetSeriesAllEpisodes(tmdbId, seriesInfo.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);

            if (seriesResponse?.seasons is null) return Array.Empty<RemoteSearchResult>();

            var localEpisodes = LocalEpisodeGroup.TryGetLocalEpisodes(tmdbId);

            if (localEpisodes != null)
            {
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

                return localEpisodes.Select(local =>
                {
                    tmdbEpisodes.TryGetValue((local.TmdbSeasonNumber, local.TmdbEpisodeNumber),
                        out var episode);

                    return ToRemoteSearchResult(local.LocalEpisodeNumber, local.LocalSeasonNumber, episode);
                }).ToArray();
            }

            return seriesResponse.seasons
                .Where(season => season?.episodes != null)
                .SelectMany(season => season.episodes)
                .Where(episode => episode != null)
                .Select(episode => ToRemoteSearchResult(episode.episode_number, episode.season_number, episode))
                .ToArray();
        }

        private static RemoteSearchResult ToRemoteSearchResult(int indexNumber, int parentIndexNumber,
            EpisodeResponseInfo episode)
        {
            var result = new RemoteSearchResult
            {
                SearchProviderName = ProviderName,
                IndexNumber = indexNumber,
                ParentIndexNumber = parentIndexNumber,
                Name = episode?.name ?? string.Empty,
                Overview = episode?.overview ?? string.Empty,
                PremiereDate = episode?.air_date,
                ProductionYear = episode?.air_date?.Year,
                ProviderIds = new ProviderIdDictionary()
            };

            if (episode != null && episode.id > 0)
            {
                result.ProviderIds[MetadataProviders.Tmdb.ToString()] =
                    episode.id.ToString(CultureInfo.InvariantCulture);
            }

            return result;
        }
    }
}
