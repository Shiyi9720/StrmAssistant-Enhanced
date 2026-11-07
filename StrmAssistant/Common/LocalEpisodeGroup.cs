using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Reads episodegroup.json files and translates local season/episode numbers to the real
    /// TMDB season/episode numbers used by the provider request.
    /// </summary>
    internal static class LocalEpisodeGroup
    {
        private const string FileName = "episodegroup.json";
        private const string OverrideFolderName = "override";
        private const string TvFolderName = "tmdb-tv";

        private static readonly Regex EpisodePathRegex =
            new Regex(@"^(/3/tv/(\d+)/season/)(\d+)(/episode/)(\d+)(.*)$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SeasonPathRegex =
            new Regex(@"^(/3/tv/(\d+)/season/)(\d+)(.*)$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly ConcurrentDictionary<string, CachedDocument> DocumentCache =
            new ConcurrentDictionary<string, CachedDocument>(StringComparer.OrdinalIgnoreCase);

        private static ILibraryManager _libraryManager;
        private static IJsonSerializer _jsonSerializer;

        public static void Initialize(ILibraryManager libraryManager, IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _jsonSerializer = jsonSerializer;
        }

        public static LocalEpisodeGroupMapping TryMapRequest(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || _jsonSerializer is null) return null;

            try
            {
                var options = Plugin.Instance?.GetPluginOptions();

                if (options is null || !options.EnableLocalEpisodeGroup) return null;

                var path = GetPath(url);
                var episodeMatch = EpisodePathRegex.Match(path);

                if (episodeMatch.Success)
                {
                    var seriesId = episodeMatch.Groups[2].Value;
                    var localSeason = int.Parse(episodeMatch.Groups[3].Value);
                    var localEpisode = int.Parse(episodeMatch.Groups[5].Value);
                    var document = LoadDocument(seriesId);
                    var group = document?.groups?.FirstOrDefault(item => item?.order == localSeason);
                    var episode = group?.episodes?.FirstOrDefault(item => item?.order == localEpisode - 1);

                    if (episode is null) return null;

                    var mappedPath = episodeMatch.Groups[1].Value + episode.season_number +
                                     episodeMatch.Groups[4].Value + episode.episode_number +
                                     episodeMatch.Groups[6].Value;

                    return new LocalEpisodeGroupMapping
                    {
                        Url = ReplacePath(url, path, mappedPath),
                        LocalSeasonNumber = localSeason,
                        LocalEpisodeNumber = localEpisode,
                        TmdbSeasonNumber = episode.season_number,
                        TmdbEpisodeNumber = episode.episode_number,
                        IsEpisodeRequest = true
                    };
                }

                var seasonMatch = SeasonPathRegex.Match(path);

                if (!seasonMatch.Success) return null;

                var seasonSeriesId = seasonMatch.Groups[2].Value;
                var season = int.Parse(seasonMatch.Groups[3].Value);
                var seasonDocument = LoadDocument(seasonSeriesId);
                var seasonGroup = seasonDocument?.groups?.FirstOrDefault(item => item?.order == season);
                var tmdbSeasons = seasonGroup?.episodes?
                    .Where(item => item != null)
                    .Select(item => item.season_number)
                    .Distinct()
                    .ToList();

                if (tmdbSeasons == null || tmdbSeasons.Count != 1) return null;

                var mappedSeasonPath = seasonMatch.Groups[1].Value + tmdbSeasons[0] + seasonMatch.Groups[4].Value;

                return new LocalEpisodeGroupMapping
                {
                    Url = ReplacePath(url, path, mappedSeasonPath),
                    LocalSeasonNumber = season,
                    TmdbSeasonNumber = tmdbSeasons[0],
                    IsEpisodeRequest = false
                };
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Local episode group - Request mapping failed: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Resolves the TMDB season/episode numbers for an item that uses the local library
        /// numbering. Returns false when the episode group is disabled or has no unambiguous
        /// mapping for the requested numbers.
        /// </summary>
        public static bool TryGetTmdbNumbers(string seriesId, int localSeason, int? localEpisode,
            out int tmdbSeason, out int? tmdbEpisode)
        {
            tmdbSeason = localSeason;
            tmdbEpisode = localEpisode;

            if (string.IsNullOrWhiteSpace(seriesId) || _jsonSerializer is null) return false;

            try
            {
                var options = Plugin.Instance?.GetPluginOptions();

                if (options is null || !options.EnableLocalEpisodeGroup) return false;

                var document = LoadDocument(seriesId);
                var group = document?.groups?.FirstOrDefault(item => item?.order == localSeason);

                if (group?.episodes is null) return false;

                if (localEpisode.HasValue)
                {
                    var episode = group.episodes.FirstOrDefault(item =>
                        item != null && item.order == localEpisode.Value - 1);

                    if (episode is null) return false;

                    tmdbSeason = episode.season_number;
                    tmdbEpisode = episode.episode_number;
                    return true;
                }

                var tmdbSeasons = group.episodes
                    .Where(item => item != null)
                    .Select(item => item.season_number)
                    .Distinct()
                    .ToList();

                if (tmdbSeasons.Count != 1) return false;

                tmdbSeason = tmdbSeasons[0];
                tmdbEpisode = null;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Local episode group - Reverse mapping failed: " + e.Message);
                return false;
            }
        }

        private static string GetPath(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.AbsolutePath;

            var queryIndex = url.IndexOf('?');
            return queryIndex >= 0 ? url.Substring(0, queryIndex) : url;
        }

        private static string ReplacePath(string url, string oldPath, string newPath)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var builder = new UriBuilder(uri) { Path = newPath };
                return builder.Uri.ToString();
            }

            return newPath + url.Substring(oldPath.Length);
        }

        private static EpisodeGroupDocument LoadDocument(string seriesId)
        {
            var path = FindFile(seriesId);

            if (path is null) return null;

            var info = new FileInfo(path);
            var stamp = info.LastWriteTimeUtc.Ticks.ToString() + ":" + info.Length;

            if (DocumentCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            {
                return cached.Document;
            }

            try
            {
                var document = _jsonSerializer.DeserializeFromFile<EpisodeGroupDocument>(path);
                DocumentCache[path] = new CachedDocument { Stamp = stamp, Document = document };
                return document;
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Local episode group - Invalid " + path + ": " + e.Message);
                return null;
            }
        }

        private static string FindFile(string seriesId)
        {
            var options = Plugin.Instance?.GetPluginOptions();
            var root = NormalizeRoot(options?.LocalMetadataRoot);

            if (root != null)
            {
                foreach (var candidate in new[]
                         {
                             Path.Combine(root, OverrideFolderName, TvFolderName, seriesId, FileName),
                             Path.Combine(root, OverrideFolderName, seriesId, FileName)
                         })
                {
                    if (File.Exists(candidate)) return candidate;
                }
            }

            try
            {
                if (_libraryManager is null) return null;

                var series = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    Recursive = true,
                    Limit = 1,
                    IncludeItemTypes = new[] { nameof(Series) },
                    AnyProviderIdEquals = new[]
                    {
                        new KeyValuePair<string, string>(MetadataProviders.Tmdb.ToString(), seriesId),
                        new KeyValuePair<string, string>("Tmdb", seriesId)
                    }
                }).OfType<Series>().FirstOrDefault();

                var directory = series?.ContainingFolderPath;

                if (string.IsNullOrWhiteSpace(directory)) directory = series?.Path;

                if (string.IsNullOrWhiteSpace(directory)) return null;

                var itemFile = Path.Combine(directory, FileName);
                return File.Exists(itemFile) ? itemFile : null;
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Local episode group - Item lookup failed: " + e.Message);
                return null;
            }
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

        private sealed class CachedDocument
        {
            public string Stamp { get; set; }
            public EpisodeGroupDocument Document { get; set; }
        }

        private sealed class EpisodeGroupDocument
        {
            public string description { get; set; }
            public List<EpisodeGroupGroup> groups { get; set; }
        }

        private sealed class EpisodeGroupGroup
        {
            public string name { get; set; }
            public string overview { get; set; }
            public string air_date { get; set; }
            public int order { get; set; }
            public List<EpisodeGroupEpisode> episodes { get; set; }
        }

        private sealed class EpisodeGroupEpisode
        {
            public int episode_number { get; set; }
            public int season_number { get; set; }
            public int order { get; set; }
        }
    }

    internal sealed class LocalEpisodeGroupMapping
    {
        public string Url { get; set; }
        public int LocalSeasonNumber { get; set; }
        public int? LocalEpisodeNumber { get; set; }
        public int TmdbSeasonNumber { get; set; }
        public int? TmdbEpisodeNumber { get; set; }
        public bool IsEpisodeRequest { get; set; }
    }
}
