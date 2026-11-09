using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Looks up already known intro/outro timestamps on the public IntroDb services. Both
    /// services index segments by IMDb id only, so an episode can be matched when its series
    /// carries an IMDb id; TMDB and TVDB ids are only used as a local fallback to find another
    /// copy of the same item that does have an IMDb id.
    ///
    /// introdb.app is asked first. When it has no usable data (not found, empty segments or a
    /// transport failure), theintrodb.org is asked as a second source. Results from both are
    /// merged per segment type with the first source taking precedence, so a title that only
    /// carries one of intro/credits on either service still gets what is available, and a
    /// segment reported by both is only used once.
    ///
    /// Results are cached on disk and feed the very same chapter marker store that the
    /// fingerprint based detection writes to, which means the caller can simply ask for markers
    /// before running fingerprint extraction. Every network or parsing problem degrades to "no
    /// data" so that a scheduled library scan never fails because of this service.
    /// </summary>
    public class IntroDbApi
    {
        private const string ApiBase = "https://api.introdb.app";
        private const string SecondaryApiBase = "https://api.theintrodb.org/v4";
        private const string StoreFileName = "StrmAssistant.IntroDb.json";
        private const string MarkerSuffix = ChapterApi.MarkerSuffix;

        private static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(30);
        private static readonly SemaphoreSlim RequestGate = new SemaphoreSlim(1, 1);
        private static readonly Regex ImdbIdPattern = new Regex(@"^tt[0-9]{7,8}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static DateTime _lastRequestUtc = DateTime.MinValue;
        // Backoff is kept per source so that a failure of the primary does not stop the
        // fallback from being queried.

        private enum SegmentSource
        {
            Primary,
            Secondary
        }

        private readonly ILogger _logger;
        private readonly IHttpClient _httpClient;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly string _storePath;
        private readonly object _sync = new object();
        private readonly Dictionary<string, IntroDbCacheEntry> _cache =
            new Dictionary<string, IntroDbCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IntroDbMovieOutroEntry> _movieEntries =
            new Dictionary<string, IntroDbMovieOutroEntry>(StringComparer.OrdinalIgnoreCase);

        private bool _dirty;
        private DateTime _lastPersistUtc = DateTime.MinValue;

        public IntroDbApi(ILibraryManager libraryManager, IItemRepository itemRepository,
            IJsonSerializer jsonSerializer, IHttpClient httpClient)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;
            _httpClient = ProxyApi.Wrap(httpClient);
            _storePath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath, StoreFileName);

            Load();
        }

        /// <summary>
        /// Applies IntroDb markers to every episode of a season that does not have a marker yet.
        /// Called right before fingerprint detection so that known timestamps skip the expensive
        /// audio analysis and unknown ones still go through it.
        /// </summary>
        public async Task ApplyToSeasonAsync(Season season, IEnumerable<Episode> episodes,
            CancellationToken cancellationToken)
        {
            try
            {
                var options = GetOptions();

                if (!options.EnableIntroDb || season is null || episodes is null) return;

                var imdbId = ResolveImdbId(season.Series);
                if (imdbId is null) return;

                var applied = 0;

                foreach (var episode in episodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (episode?.IndexNumber is null || episode.ParentIndexNumber is null) continue;
                    // IntroDb only accepts season numbers starting at 1, specials have no data.
                    if (episode.ParentIndexNumber < 1 || episode.IndexNumber < 1) continue;

                    var entry = await GetOrFetchAsync(GetEpisodeCacheKey(imdbId, episode.ParentIndexNumber.Value,
                            episode.IndexNumber.Value), imdbId, episode.ParentIndexNumber, episode.IndexNumber,
                        false, cancellationToken).ConfigureAwait(false);

                    if (entry is null || !entry.Found) continue;

                    if (ApplyMarkers(episode, entry, options)) applied++;
                }

                if (applied > 0)
                {
                    _logger?.Info("IntroDb - Applied markers for " + applied + " episodes in " + season.Name +
                                  " - " + season.Path);
                }

                PersistIfNeeded();
            }
            catch (OperationCanceledException)
            {
                // The caller owns cancellation; nothing to clean up here.
            }
            catch (Exception e)
            {
                _logger?.Debug("IntroDb - Season lookup skipped: " + e.Message);
            }
        }

        /// <summary>
        /// Single episode variant, useful for callers that work item by item instead of season by
        /// season.
        /// </summary>
        public async Task<bool> ApplyToEpisodeAsync(Episode episode, CancellationToken cancellationToken)
        {
            try
            {
                var options = GetOptions();

                if (!options.EnableIntroDb || episode?.IndexNumber is null ||
                    episode.ParentIndexNumber is null || episode.ParentIndexNumber < 1) return false;

                var imdbId = ResolveImdbId(episode.Series);
                if (imdbId is null) return false;

                var entry = await GetOrFetchAsync(GetEpisodeCacheKey(imdbId, episode.ParentIndexNumber.Value,
                        episode.IndexNumber.Value), imdbId, episode.ParentIndexNumber, episode.IndexNumber,
                    false, cancellationToken).ConfigureAwait(false);

                var applied = entry != null && entry.Found && ApplyMarkers(episode, entry, options);

                PersistIfNeeded();

                return applied;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception e)
            {
                _logger?.Debug("IntroDb - Episode lookup skipped: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Movie variant. Emby's intro skip only applies to episodes, so movie credits are not
        /// written as chapter markers. The resolved credits range is stored in the plugin's own
        /// JSON store instead and can be used by a future display surface.
        /// </summary>
        public async Task<bool> ApplyToMovieAsync(Movie movie, CancellationToken cancellationToken)
        {
            try
            {
                var options = GetOptions();

                if (!options.EnableMovieIntroDb || movie is null || movie.InternalId == 0) return false;

                var imdbId = ResolveImdbId(movie);
                if (imdbId is null)
                {
                    _logger?.Debug("IntroDb - Movie skipped, no IMDb id: " + movie.Name);
                    return false;
                }

                var entry = await GetOrFetchAsync(GetMovieCacheKey(imdbId), imdbId, null, null, true,
                    cancellationToken).ConfigureAwait(false);

                PersistIfNeeded();

                if (entry is null || !entry.Found || !entry.HasOutro())
                {
                    _logger?.Debug("IntroDb - No movie credits: " + imdbId + " - " + movie.Name);
                    return false;
                }

                if (!PassesConfidence(entry.OutroConfidence, options))
                {
                    _logger?.Debug("IntroDb - Movie credits below confidence threshold: " + imdbId + " - " +
                                   movie.Name);
                    return false;
                }

                var startTicks = ToTicks(entry.OutroStartMs);
                var endTicks = entry.OutroEndOpen ? movie.RunTimeTicks ?? 0 : ToTicks(entry.OutroEndMs);

                if (movie.RunTimeTicks.HasValue && endTicks > movie.RunTimeTicks.Value)
                {
                    endTicks = movie.RunTimeTicks.Value;
                }

                if (startTicks <= 0 || endTicks <= startTicks ||
                    TimeSpan.FromTicks(endTicks - startTicks) < TimeSpan.FromSeconds(5) ||
                    movie.RunTimeTicks.HasValue && startTicks >= movie.RunTimeTicks.Value)
                {
                    _logger?.Debug("IntroDb - Movie credits ignored, invalid range: " + imdbId + " - " +
                                   movie.Name);
                    return false;
                }

                var endMilliseconds = endTicks / TimeSpan.TicksPerMillisecond;

                var record = new IntroDbMovieOutroEntry
                {
                    ItemId = movie.InternalId,
                    ImdbId = imdbId,
                    Name = movie.Name,
                    Path = movie.Path,
                    OutroStartMs = entry.OutroStartMs,
                    OutroEndMs = endMilliseconds > int.MaxValue ? int.MaxValue : (int) endMilliseconds,
                    OutroEndOpen = entry.OutroEndOpen,
                    OutroConfidence = entry.OutroConfidence,
                    UpdatedAtUtc = DateTime.UtcNow
                };

                lock (_sync)
                {
                    _movieEntries[movie.InternalId.ToString()] = record;
                    _dirty = true;
                }

                PersistIfNeeded(true);

                _logger?.Info("IntroDb - Movie credits recorded: " + movie.Name + " (" + imdbId + ") " +
                              TimeSpan.FromTicks(startTicks).ToString(@"hh\:mm\:ss\.fff") + " - " +
                              TimeSpan.FromTicks(endTicks).ToString(@"hh\:mm\:ss\.fff"));

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception e)
            {
                _logger?.Debug("IntroDb - Movie lookup skipped: " + e.Message);
                return false;
            }
        }

        private static IntroDbOptions GetOptions()
        {
            return Plugin.Instance?.GetPluginOptions()?.IntroSkipOptions?.IntroDbOptions ?? new IntroDbOptions();
        }

        private static string GetEpisodeCacheKey(string imdbId, int season, int episode)
        {
            return "tv|" + imdbId + "|" + season + "|" + episode;
        }

        private static string GetMovieCacheKey(string imdbId)
        {
            return "movie|" + imdbId;
        }

        /// <summary>
        /// Returns the recorded movie credits for an item, or null when no valid range was stored.
        /// </summary>
        public IntroDbMovieOutroEntry GetMovieOutro(long itemId)
        {
            lock (_sync)
            {
                return _movieEntries.TryGetValue(itemId.ToString(), out var entry) ? entry : null;
            }
        }

        private string ResolveImdbId(BaseItem item)
        {
            if (item is null) return null;

            var imdbId = NormalizeImdbId(item.GetProviderId(MetadataProviders.Imdb));
            if (imdbId != null) return imdbId;

            // IntroDb is IMDb-only. When an item was matched by TMDB or TVDB only, try to find a
            // local copy of the same item that already carries an IMDb id (for example a merged
            // multi-version entry) instead of calling any additional web service.
            foreach (var provider in new[] { MetadataProviders.Tmdb, MetadataProviders.Tvdb })
            {
                var providerId = item.GetProviderId(provider);
                if (string.IsNullOrEmpty(providerId)) continue;

                try
                {
                    var match = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        Recursive = true,
                        Limit = 5,
                        IncludeItemTypes = new[] { item.GetType().Name },
                        AnyProviderIdEquals = new[]
                        {
                            new KeyValuePair<string, string>(provider.ToString(), providerId)
                        }
                    }).FirstOrDefault(i => !string.IsNullOrEmpty(i.GetProviderId(MetadataProviders.Imdb)));

                    var resolved = NormalizeImdbId(match?.GetProviderId(MetadataProviders.Imdb));
                    if (resolved != null) return resolved;
                }
                catch (Exception e)
                {
                    _logger?.Debug("IntroDb - Provider id lookup failed: " + e.Message);
                }
            }

            return null;
        }

        private static string NormalizeImdbId(string imdbId)
        {
            if (string.IsNullOrWhiteSpace(imdbId)) return null;

            imdbId = imdbId.Trim();

            return ImdbIdPattern.IsMatch(imdbId) ? imdbId : null;
        }

        private async Task<IntroDbCacheEntry> GetOrFetchAsync(string cacheKey, string imdbId, int? season,
            int? episode, bool isMovie, CancellationToken cancellationToken)
        {
            var cached = TryGetCached(cacheKey);
            if (cached != null) return cached;

            var entry = await RequestSegmentsAsync(imdbId, season, episode, isMovie, cancellationToken)
                .ConfigureAwait(false);

            if (entry is null) return null;

            entry.FetchedAtUtc = DateTime.UtcNow;

            lock (_sync)
            {
                _cache[cacheKey] = entry;
                _dirty = true;
            }

            return entry;
        }

        private IntroDbCacheEntry TryGetCached(string cacheKey)
        {
            var expiry = TimeSpan.FromDays(GetOptions().CacheExpiryDays);

            lock (_sync)
            {
                if (!_cache.TryGetValue(cacheKey, out var entry) || entry is null) return null;

                if (DateTime.UtcNow - entry.FetchedAtUtc <= expiry) return entry;

                _cache.Remove(cacheKey);
                _dirty = true;

                return null;
            }
        }

        private async Task<IntroDbCacheEntry> RequestSegmentsAsync(string imdbId, int? season, int? episode,
            bool isMovie, CancellationToken cancellationToken)
        {
            var primary = await RequestSourceAsync(SegmentSource.Primary, imdbId, season, episode, isMovie,
                cancellationToken).ConfigureAwait(false);

            if (primary != null && primary.HasAnySegment()) return primary;

            var secondary = await RequestSourceAsync(SegmentSource.Secondary, imdbId, season, episode, isMovie,
                cancellationToken).ConfigureAwait(false);

            if (secondary != null && secondary.HasAnySegment())
            {
                return primary is null ? secondary : MergeEntries(primary, secondary);
            }

            // Cache a negative result only when the primary actually answered "not found". A
            // primary that failed (timeout, 5xx, circuit open) must be retried on the next scan,
            // even when the secondary had nothing either.
            return primary;
        }

        /// <summary>
        /// Merges the two sources per segment type. The primary keeps both segment types it has;
        /// the secondary only contributes types the primary did not provide. This also removes
        /// duplicates: a segment present in both sources is written once, from the primary.
        /// </summary>
        private static IntroDbCacheEntry MergeEntries(IntroDbCacheEntry primary, IntroDbCacheEntry secondary)
        {
            var entry = new IntroDbCacheEntry { Found = true };

            if (primary.HasIntro() || !secondary.HasIntro())
            {
                entry.IntroStartMs = primary.IntroStartMs;
                entry.IntroEndMs = primary.IntroEndMs;
                entry.IntroConfidence = primary.IntroConfidence;
                entry.IntroSubmissionCount = primary.IntroSubmissionCount;
            }
            else
            {
                entry.IntroStartMs = secondary.IntroStartMs;
                entry.IntroEndMs = secondary.IntroEndMs;
                entry.IntroConfidence = secondary.IntroConfidence;
                entry.IntroSubmissionCount = secondary.IntroSubmissionCount;
            }

            if (primary.HasOutro() || !secondary.HasOutro())
            {
                entry.OutroStartMs = primary.OutroStartMs;
                entry.OutroEndMs = primary.OutroEndMs;
                entry.OutroEndOpen = primary.OutroEndOpen;
                entry.OutroConfidence = primary.OutroConfidence;
                entry.OutroSubmissionCount = primary.OutroSubmissionCount;
            }
            else
            {
                entry.OutroStartMs = secondary.OutroStartMs;
                entry.OutroEndMs = secondary.OutroEndMs;
                entry.OutroEndOpen = secondary.OutroEndOpen;
                entry.OutroConfidence = secondary.OutroConfidence;
                entry.OutroSubmissionCount = secondary.OutroSubmissionCount;
            }

            return entry;
        }

        private async Task<IntroDbCacheEntry> RequestSourceAsync(SegmentSource source, string imdbId, int? season,
            int? episode, bool isMovie, CancellationToken cancellationToken)
        {
            if (DateTime.UtcNow < GetUnavailableUntil(source)) return null;

            await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (DateTime.UtcNow < GetUnavailableUntil(source)) return null;

                var elapsed = DateTime.UtcNow - _lastRequestUtc;
                if (elapsed < RequestInterval)
                {
                    await Task.Delay(RequestInterval - elapsed, cancellationToken).ConfigureAwait(false);
                }

                var options = new HttpRequestOptions
                {
                    Url = BuildUrl(source, imdbId, season, episode, isMovie),
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = "StrmAssistant",
                    BufferContent = true,
                    TimeoutMs = 10000,
                    ThrowOnErrorResponse = false,
                    LogRequest = false,
                    LogResponse = false,
                    LogErrors = false
                };

                using var response = await _httpClient.SendAsync(options, "GET").ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Not an error: the database simply has no submission for this episode.
                    return new IntroDbCacheEntry { Found = false };
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    if ((int) response.StatusCode >= 500 || response.StatusCode == (HttpStatusCode) 429)
                    {
                        SetUnavailable(source);
                    }

                    _logger?.Debug("IntroDb - " + source + " returned " + (int)response.StatusCode + " for " +
                                   imdbId + " S" + season + "E" + episode);
                    // Do not cache failures, the service may be temporarily unavailable.
                    return null;
                }

                await using var contentStream = response.Content;

                var entry = source == SegmentSource.Primary
                    ? ParsePrimary(_jsonSerializer.DeserializeFromStream<IntroDbSegments>(contentStream))
                    : ParseSecondary(_jsonSerializer.DeserializeFromStream<TheIntroDbMedia>(contentStream));

                SourceBackoff.ReportSuccess(BackoffKey(source));

                return entry;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A request timeout surfaces as a cancellation that is not owned by the caller.
                SetUnavailable(source);
                _logger?.Debug("IntroDb - " + source + " request timed out");
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                SetUnavailable(source);
                _logger?.Debug("IntroDb - " + source + " request failed: " + e.Message);
                return null;
            }
            finally
            {
                _lastRequestUtc = DateTime.UtcNow;
                RequestGate.Release();
            }
        }

        private static string BuildUrl(SegmentSource source, string imdbId, int? season, int? episode, bool isMovie)
        {
            var query = "imdb_id=" + Uri.EscapeDataString(imdbId);

            if (source == SegmentSource.Primary)
            {
                if (season.HasValue) query += "&season=" + season.Value;
                if (episode.HasValue) query += "&episode=" + episode.Value;
                if (isMovie) query += "&is_movie=true";

                return ApiBase + "/segments?" + query;
            }

            // theintrodb.org serves movies and episodes from /media; a movie has no season or
            // episode parameters. details=true adds the per-segment confidence we filter on.
            if (!isMovie)
            {
                if (season.HasValue) query += "&season=" + season.Value;
                if (episode.HasValue) query += "&episode=" + episode.Value;
            }

            return SecondaryApiBase + "/media?" + query + "&details=true";
        }

        private static DateTime GetUnavailableUntil(SegmentSource source)
        {
            return SourceBackoff.UnavailableUntil(BackoffKey(source)) ?? DateTime.MinValue;
        }

        private static string BackoffKey(SegmentSource source)
        {
            return "introdb:" + source;
        }

        private static void SetUnavailable(SegmentSource source)
        {
            SourceBackoff.ReportFailure(BackoffKey(source));
        }

        private static IntroDbCacheEntry ParsePrimary(IntroDbSegments segments)
        {
            if (segments is null) return new IntroDbCacheEntry { Found = false };

            return new IntroDbCacheEntry
            {
                Found = true,
                IntroStartMs = segments.intro?.StartMilliseconds ?? 0,
                IntroEndMs = segments.intro?.EndMilliseconds ?? 0,
                IntroConfidence = segments.intro?.confidence ?? 0,
                IntroSubmissionCount = segments.intro?.submission_count ?? 0,
                OutroStartMs = segments.outro?.StartMilliseconds ?? 0,
                OutroEndMs = segments.outro?.EndMilliseconds ?? 0,
                OutroConfidence = segments.outro?.confidence ?? 0,
                OutroSubmissionCount = segments.outro?.submission_count ?? 0
            };
        }

        private static IntroDbCacheEntry ParseSecondary(TheIntroDbMedia media)
        {
            if (media is null) return new IntroDbCacheEntry { Found = false };

            // Both segment lists may hold several entries; a segment confirmed absent is sent as
            // 0/0 and is skipped here. Only one intro and one credits range fit the marker model.
            var intro = PickSegment(media.intro, true);
            var credits = PickSegment(media.credits, false);

            if (intro is null && credits is null) return new IntroDbCacheEntry { Found = false };

            var entry = new IntroDbCacheEntry { Found = true };

            if (intro != null)
            {
                entry.IntroStartMs = ToMilliseconds(intro.start_ms);
                entry.IntroEndMs = ToMilliseconds(intro.end_ms);
                entry.IntroConfidence = intro.confidence ?? 0;
                entry.IntroSubmissionCount = intro.submission_count ?? 0;
            }

            if (credits != null)
            {
                entry.OutroStartMs = ToMilliseconds(credits.start_ms);

                // A null end means "runs to the end of the media", which the marker writer
                // resolves against the item duration.
                entry.OutroEndOpen = !credits.end_ms.HasValue;
                entry.OutroEndMs = credits.end_ms.HasValue ? ToMilliseconds(credits.end_ms) : 0;
                entry.OutroConfidence = credits.confidence ?? 0;
                entry.OutroSubmissionCount = credits.submission_count ?? 0;
            }

            return entry;
        }

        private static TheIntroDbSegment PickSegment(TheIntroDbSegment[] segments, bool requireEnd)
        {
            if (segments is null) return null;

            foreach (var segment in segments)
            {
                if (segment is null) continue;

                var start = segment.start_ms ?? 0;

                if (start < 0) continue;

                if (!requireEnd)
                {
                    // Credits always carry a start; a missing end means "to the end of the media".
                    if (start > 0 && (!segment.end_ms.HasValue || segment.end_ms.Value > start))
                    {
                        return segment;
                    }
                }
                else if (segment.end_ms.HasValue && segment.end_ms.Value > start)
                {
                    return segment;
                }
            }

            return null;
        }

        private static int ToMilliseconds(long? value)
        {
            if (!value.HasValue || value.Value <= 0) return 0;

            return value.Value > int.MaxValue ? int.MaxValue : (int)value.Value;
        }

        private bool ApplyMarkers(BaseItem item, IntroDbCacheEntry entry, IntroDbOptions options)
        {
            var introSkipOptions = Plugin.Instance.GetPluginOptions().IntroSkipOptions;
            var changed = false;

            // Chapter marker writes must stay serialized, the fingerprint pipeline uses the very
            // same lock while it saves markers for a whole season.
            lock (ChapterApi.MarkerWriteLock)
            {
                var chapters = _itemRepository.GetChapters(item);

                if (options.WriteIntroMarkers && IsIntroAcceptable(item, introSkipOptions, entry, options) &&
                    IntroMarkerHelper.TryNormalizeRange(item, introSkipOptions, ToTicks(entry.IntroStartMs),
                        ToTicks(entry.IntroEndMs), out var introStartTicks, out var introEndTicks))
                {
                    var canWrite = chapters
                        .Where(c => c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd)
                        .All(IsMarkerAddedByIntroSkip);

                    if (canWrite)
                    {
                        chapters.RemoveAll(c => c.MarkerType == MarkerType.IntroStart ||
                                                c.MarkerType == MarkerType.IntroEnd);
                        chapters.Add(new ChapterInfo
                        {
                            Name = MarkerType.IntroStart + MarkerSuffix,
                            MarkerType = MarkerType.IntroStart,
                            StartPositionTicks = introStartTicks
                        });
                        chapters.Add(new ChapterInfo
                        {
                            Name = MarkerType.IntroEnd + MarkerSuffix,
                            MarkerType = MarkerType.IntroEnd,
                            StartPositionTicks = introEndTicks
                        });
                        changed = true;
                    }
                }

                if (options.WriteCreditsMarkers && IsCreditsAcceptable(item, introSkipOptions, entry, options))
                {
                    var canWrite = chapters
                        .Where(c => c.MarkerType == MarkerType.CreditsStart)
                        .All(IsMarkerAddedByIntroSkip);

                    if (canWrite)
                    {
                        chapters.RemoveAll(c => c.MarkerType == MarkerType.CreditsStart);
                        chapters.Add(new ChapterInfo
                        {
                            Name = MarkerType.CreditsStart + MarkerSuffix,
                            MarkerType = MarkerType.CreditsStart,
                            StartPositionTicks = ToTicks(entry.OutroStartMs)
                        });
                        changed = true;
                    }
                }

                if (changed)
                {
                    chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                    _itemRepository.SaveChapters(item.InternalId, chapters);
                }
            }

            return changed;
        }

        private static bool IsIntroAcceptable(BaseItem item, IntroSkipOptions introSkipOptions,
            IntroDbCacheEntry entry, IntroDbOptions options)
        {
            if (!entry.HasIntro()) return false;
            if (!PassesConfidence(entry.IntroConfidence, options)) return false;

            var start = ToTicks(entry.IntroStartMs);
            var end = ToTicks(entry.IntroEndMs);

            if (start < 0 || end <= start) return false;

            var duration = TimeSpan.FromTicks(end - start);

            var minSeconds = Math.Max(1, introSkipOptions.IntroMarkerMinDurationSeconds);
            var maxSeconds = Math.Max(1, Math.Min(introSkipOptions.MaxIntroDurationSeconds,
                introSkipOptions.IntroMarkerMaxDurationSeconds));

            if (duration < TimeSpan.FromSeconds(minSeconds)) return false;
            if (duration > TimeSpan.FromSeconds(maxSeconds)) return false;

            return item.RunTimeTicks is null || end <= item.RunTimeTicks.Value;
        }

        private static bool IsCreditsAcceptable(BaseItem item, IntroSkipOptions introSkipOptions,
            IntroDbCacheEntry entry, IntroDbOptions options)
        {
            if (!entry.HasOutro()) return false;
            if (!PassesConfidence(entry.OutroConfidence, options)) return false;

            var start = ToTicks(entry.OutroStartMs);
            var end = ToTicks(entry.OutroEndMs);

            // An open-ended credits segment runs to the end of the media, so its end comes from
            // the item duration. Without a known duration the segment cannot be validated.
            if (entry.OutroEndOpen) end = item.RunTimeTicks ?? 0;

            if (start <= 0 || end <= start) return false;

            var duration = TimeSpan.FromTicks(end - start);

            if (duration < TimeSpan.FromSeconds(5)) return false;
            if (duration > TimeSpan.FromSeconds(introSkipOptions.MaxCreditsDurationSeconds)) return false;

            return item.RunTimeTicks is null || start < item.RunTimeTicks.Value - TimeSpan.FromSeconds(5).Ticks;
        }

        private static bool PassesConfidence(double confidence, IntroDbOptions options)
        {
            // A zero confidence means the field was not provided; accept it rather than throwing
            // away otherwise valid data.
            if (double.IsNaN(confidence) || confidence <= 0) return true;

            var minimum = Math.Max(0, Math.Min(100, options.MinimumConfidencePercent));

            return confidence * 100 >= minimum;
        }

        private static bool IsMarkerAddedByIntroSkip(ChapterInfo chapter)
        {
            return chapter.Name != null && chapter.Name.EndsWith(MarkerSuffix, StringComparison.Ordinal);
        }

        private static long ToTicks(int milliseconds)
        {
            return milliseconds * TimeSpan.TicksPerMillisecond;
        }

        private void PersistIfNeeded(bool force = false)
        {
            Dictionary<string, IntroDbCacheEntry> snapshot;
            Dictionary<string, IntroDbMovieOutroEntry> movieSnapshot;

            lock (_sync)
            {
                if (!_dirty || (!force && DateTime.UtcNow - _lastPersistUtc < PersistInterval)) return;

                snapshot = new Dictionary<string, IntroDbCacheEntry>(_cache, StringComparer.OrdinalIgnoreCase);
                movieSnapshot =
                    new Dictionary<string, IntroDbMovieOutroEntry>(_movieEntries, StringComparer.OrdinalIgnoreCase);
                _dirty = false;
                _lastPersistUtc = DateTime.UtcNow;
            }

            try
            {
                _jsonSerializer.SerializeToFile(new IntroDbCacheStore
                {
                    Entries = snapshot,
                    MovieOutros = movieSnapshot
                }, _storePath);
            }
            catch (Exception e)
            {
                _logger?.Debug("IntroDb - Failed to save cache: " + e.Message);

                lock (_sync)
                {
                    _dirty = true;
                }
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_storePath)) return;

                var store = _jsonSerializer.DeserializeFromFile<IntroDbCacheStore>(_storePath);
                if (store is null) return;

                var expiry = TimeSpan.FromDays(GetOptions().CacheExpiryDays);

                lock (_sync)
                {
                    if (store.Entries != null)
                    {
                        foreach (var pair in store.Entries)
                        {
                            if (pair.Value is null) continue;
                            if (DateTime.UtcNow - pair.Value.FetchedAtUtc > expiry) continue;

                            _cache[pair.Key] = pair.Value;
                        }
                    }

                    if (store.MovieOutros != null)
                    {
                        foreach (var pair in store.MovieOutros)
                        {
                            if (!string.IsNullOrEmpty(pair.Key) && pair.Value != null)
                            {
                                _movieEntries[pair.Key] = pair.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger?.Debug("IntroDb - Failed to load cache: " + e.Message);
            }
        }

        // The IntroDb API uses snake_case field names. Matching them exactly keeps the DTO
        // deserializable without depending on any serializer naming convention.
        public class IntroDbSegments
        {
            public string imdb_id { get; set; }
            public string media_type { get; set; }
            public bool is_movie { get; set; }
            public int? season { get; set; }
            public int? episode { get; set; }
            public IntroDbSegment intro { get; set; }
            public IntroDbSegment recap { get; set; }
            public IntroDbSegment outro { get; set; }
            public IntroDbSegment post_credits { get; set; }
            public bool no_intro { get; set; }
            public bool no_recap { get; set; }
            public bool no_outro { get; set; }
        }

        public class IntroDbSegment
        {
            public int start_ms { get; set; }
            public int end_ms { get; set; }
            public double start_sec { get; set; }
            public double end_sec { get; set; }
            public double confidence { get; set; }
            public int submission_count { get; set; }

            public int StartMilliseconds =>
                start_ms > 0 ? start_ms : (int) Math.Round(Math.Max(0, start_sec) * 1000);

            public int EndMilliseconds =>
                end_ms > 0 ? end_ms : (int) Math.Round(Math.Max(0, end_sec) * 1000);
        }

        public class IntroDbCacheStore
        {
            public Dictionary<string, IntroDbCacheEntry> Entries { get; set; } =
                new Dictionary<string, IntroDbCacheEntry>(StringComparer.OrdinalIgnoreCase);

            // Item level movie credits are kept beside the query cache so that they survive the
            // cache expiry and do not depend on any Emby field that movies do not support.
            public Dictionary<string, IntroDbMovieOutroEntry> MovieOutros { get; set; } =
                new Dictionary<string, IntroDbMovieOutroEntry>(StringComparer.OrdinalIgnoreCase);
        }

        public class IntroDbMovieOutroEntry
        {
            public long ItemId { get; set; }
            public string ImdbId { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
            public int OutroStartMs { get; set; }
            public int OutroEndMs { get; set; }
            public bool OutroEndOpen { get; set; }
            public double OutroConfidence { get; set; }
            public DateTime UpdatedAtUtc { get; set; }
        }

        // theintrodb.org (v4 /media) returns one object per title with an array per segment type;
        // each array may be absent, and a segment may omit its start or end timestamp. A segment
        // confirmed absent is sent as 0/0 and is discarded when picking the range.
        public class TheIntroDbMedia
        {
            public long? tvdb_id { get; set; }
            public long? tmdb_id { get; set; }
            public string imdb_id { get; set; }
            public string type { get; set; }
            public int? season { get; set; }
            public int? episode { get; set; }
            public string title { get; set; }
            public TheIntroDbSegment[] intro { get; set; }
            public TheIntroDbSegment[] recap { get; set; }
            public TheIntroDbSegment[] credits { get; set; }
            public TheIntroDbSegment[] preview { get; set; }
        }

        public class TheIntroDbSegment
        {
            public long? start_ms { get; set; }
            public long? end_ms { get; set; }
            public double? confidence { get; set; }
            public int? submission_count { get; set; }
        }

        public class IntroDbCacheEntry
        {
            public DateTime FetchedAtUtc { get; set; }
            public bool Found { get; set; }
            public int IntroStartMs { get; set; }
            public int IntroEndMs { get; set; }
            public double IntroConfidence { get; set; }
            public int IntroSubmissionCount { get; set; }
            public int OutroStartMs { get; set; }
            public int OutroEndMs { get; set; }
            // The second source may report credits that run to the end of the media (no end
            // timestamp). Such a segment has no end until it is resolved against the item.
            public bool OutroEndOpen { get; set; }
            public double OutroConfidence { get; set; }
            public int OutroSubmissionCount { get; set; }

            public bool HasIntro()
            {
                return IntroEndMs > IntroStartMs;
            }

            public bool HasOutro()
            {
                return OutroEndOpen ? OutroStartMs > 0 : OutroEndMs > OutroStartMs;
            }

            public bool HasAnySegment()
            {
                return HasIntro() || HasOutro();
            }
        }
    }
}
