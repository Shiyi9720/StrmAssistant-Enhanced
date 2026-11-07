using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Implements the first slice of the "local TMDB metadata source": a passive cache and an
    /// override cache rooted at a user supplied directory. It sits in the TMDB HTTP client so
    /// Emby still parses ordinary TMDB JSON responses, but those responses may come from disk.
    /// </summary>
    internal static class LocalMetadataSource
    {
        private const string CacheFolderName = "cache";
        private const string OverrideFolderName = "override";
        private const string MoviesFolderName = "tmdb-movies2";
        private const string TvFolderName = "tmdb-tv";
        private const string CollectionsFolderName = "tmdb-collections";
        private const string ImagesFolderName = "images";
        private const int FileLockStripes = 64;
        private const int SummaryDelayMilliseconds = 5000;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly Regex MovieTargetRegex =
            new Regex(@"^/3/movie/(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TvTargetRegex =
            new Regex(@"^/3/tv/(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CollectionTargetRegex =
            new Regex(@"^/3/collection/(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SeasonTargetRegex =
            new Regex(@"^/3/tv/(\d+)/season/(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EpisodeTargetRegex =
            new Regex(@"^/3/tv/(\d+)/season/(\d+)/episode/(\d+)$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly SemaphoreSlim[] FileLocks = Enumerable.Range(0, FileLockStripes)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();
        private static readonly object SummaryTimerLock = new object();

        private static ILogger _logger;
        private static IJsonSerializer _jsonSerializer;
        private static IHttpClient _httpClient;
        private static Timer _summaryTimer;

        private static long _overrideHits;
        private static long _cacheHits;
        private static long _onlineRequests;
        private static long _metadataWrites;
        private static long _imageWrites;
        private static long _failures;

        public static void Initialize(ILogger logger, IJsonSerializer jsonSerializer, IHttpClient httpClient)
        {
            _logger = logger;
            _jsonSerializer = jsonSerializer;
            _httpClient = httpClient;
            ApplyOptions(GetOptions());
        }

        public static void ApplyOptions(PluginOptions options)
        {
            if (options is null) return;

            try
            {
                var root = NormalizeRoot(options.LocalMetadataRoot);

                if (root is null) return;

                CreateDirectories(root);

                if (!options.EnableLocalMetadataSource) return;

                _logger?.Info("Local metadata source is enabled - Root: {0}, Mode: {1}, AutoRefreshImages: {2}",
                    root, options.LocalMetadataMode, options.LocalMetadataAutoRefreshImages);
            }
            catch (Exception e)
            {
                LogDebug("Local metadata source - Options initialization failed", e);
            }
        }

        public static LocalMetadataTarget TryParseTarget(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || GetActiveConfig() is null) return null;

            try
            {
                string path;

                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    path = uri.AbsolutePath;
                }
                else
                {
                    path = url;
                    var queryIndex = path.IndexOf('?');

                    if (queryIndex >= 0)
                    {
                        path = path.Substring(0, queryIndex);
                    }
                }

                path = path.TrimEnd('/');
                var tmdbRootIndex = path.IndexOf("/3/", StringComparison.OrdinalIgnoreCase);

                if (tmdbRootIndex >= 0)
                {
                    path = path.Substring(tmdbRootIndex);
                }
                else if (!path.StartsWith("/3/", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var match = EpisodeTargetRegex.Match(path);

                if (match.Success)
                {
                    var seriesId = match.Groups[1].Value;
                    var season = match.Groups[2].Value;
                    var episode = match.Groups[3].Value;

                    return new LocalMetadataTarget
                    {
                        Kind = LocalMetadataTargetKind.Episode,
                        TmdbId = seriesId,
                        RequestUrl = url,
                        RelativePaths = new[]
                        {
                            $"{TvFolderName}/{seriesId}/season-{season}-episode-{episode}.json",
                            $"{TvFolderName}/{seriesId}/episode-{season}-{episode}.json"
                        }
                    };
                }

                match = SeasonTargetRegex.Match(path);

                if (match.Success)
                {
                    var seriesId = match.Groups[1].Value;
                    var season = match.Groups[2].Value;

                    return new LocalMetadataTarget
                    {
                        Kind = LocalMetadataTargetKind.Season,
                        TmdbId = seriesId,
                        RequestUrl = url,
                        RelativePaths = new[] { $"{TvFolderName}/{seriesId}/season-{season}.json" }
                    };
                }

                match = MovieTargetRegex.Match(path);

                if (match.Success)
                {
                    var id = match.Groups[1].Value;

                    return new LocalMetadataTarget
                    {
                        Kind = LocalMetadataTargetKind.Movie,
                        TmdbId = id,
                        RequestUrl = url,
                        RelativePaths = new[]
                        {
                            $"{MoviesFolderName}/{id}/movie.json",
                            $"{MoviesFolderName}/{id}/all.json"
                        }
                    };
                }

                match = TvTargetRegex.Match(path);

                if (match.Success)
                {
                    var id = match.Groups[1].Value;

                    return new LocalMetadataTarget
                    {
                        Kind = LocalMetadataTargetKind.Series,
                        TmdbId = id,
                        RequestUrl = url,
                        RelativePaths = new[] { $"{TvFolderName}/{id}/series.json" }
                    };
                }

                match = CollectionTargetRegex.Match(path);

                if (match.Success)
                {
                    var id = match.Groups[1].Value;

                    return new LocalMetadataTarget
                    {
                        Kind = LocalMetadataTargetKind.Collection,
                        TmdbId = id,
                        RequestUrl = url,
                        RelativePaths = new[]
                        {
                            $"{CollectionsFolderName}/{id}/collection.json",
                            $"{CollectionsFolderName}/{id}/all.json"
                        }
                    };
                }
            }
            catch (Exception e)
            {
                LogDebug("Local metadata source - Target parsing failed", e);
            }

            return null;
        }

        public static HttpResponseInfo TryCreateResponse(LocalMetadataTarget target)
        {
            if (target is null) return null;

            var config = GetActiveConfig();

            if (config is null) return null;

            foreach (var relativePath in target.RelativePaths)
            {
                var path = Combine(config.Root, OverrideFolderName, relativePath);
                var bytes = TryReadAllBytes(path);

                if (bytes is null) continue;

                Interlocked.Increment(ref _overrideHits);
                ScheduleSummary();
                LogDebug("Local metadata override hit - " + relativePath);

                return CreateResponse(target, bytes, "override");
            }

            if (config.Mode != LocalMetadataMode.CacheFirst) return null;

            foreach (var relativePath in target.RelativePaths)
            {
                var path = Combine(config.Root, CacheFolderName, relativePath);
                var bytes = TryReadAllBytes(path);

                if (bytes is null) continue;

                Interlocked.Increment(ref _cacheHits);
                ScheduleSummary();
                LogDebug("Local metadata cache hit - " + relativePath);

                return CreateResponse(target, bytes, "cache");
            }

            return null;
        }

        public static void RecordOnlineRequest()
        {
            Interlocked.Increment(ref _onlineRequests);
            ScheduleSummary();
        }

        public static async Task CaptureAsync(LocalMetadataTarget target, HttpResponseInfo response,
            LocalMetadataCaptureContext context = null)
        {
            if (target is null || response?.Content is null || !IsSuccessful(response)) return;

            var config = GetActiveConfig();

            if (config is null ||
                (config.Mode != LocalMetadataMode.CacheFirst &&
                 config.Mode != LocalMetadataMode.OnlineWithCache))
            {
                return;
            }

            try
            {
                var body = await ReadBodyAsync(response).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(body)) return;

                var capturedBody = AttachMetadata(body, target, context);
                var relativePath = target.RelativePaths[0];
                var path = Combine(config.Root, CacheFolderName, relativePath);
                var metadataChanged = WriteTextFile(path, capturedBody);

                if (metadataChanged)
                {
                    Interlocked.Increment(ref _metadataWrites);
                    ScheduleSummary();
                    LogDebug("Local metadata captured - " + relativePath);
                }

                if (config.AutoRefreshImages)
                {
                    await CaptureImagesAsync(config, target, capturedBody).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failures);
                ScheduleSummary();
                LogDebug("Local metadata capture failed - " + target.RelativePaths[0], e);
            }
        }

        public static void FlushStatistics(string reason)
        {
            var overrideHits = Interlocked.Exchange(ref _overrideHits, 0);
            var cacheHits = Interlocked.Exchange(ref _cacheHits, 0);
            var onlineRequests = Interlocked.Exchange(ref _onlineRequests, 0);
            var metadataWrites = Interlocked.Exchange(ref _metadataWrites, 0);
            var imageWrites = Interlocked.Exchange(ref _imageWrites, 0);
            var failures = Interlocked.Exchange(ref _failures, 0);

            if (overrideHits == 0 && cacheHits == 0 && onlineRequests == 0 &&
                metadataWrites == 0 && imageWrites == 0 && failures == 0)
            {
                return;
            }

            try
            {
                _logger?.Info(
                    "Local metadata source ({0}) - Override hits: {1}, Cache hits: {2}, Online requests: {3}, Metadata writes: {4}, Image writes: {5}, Failures: {6}",
                    reason, overrideHits, cacheHits, onlineRequests, metadataWrites, imageWrites, failures);
            }
            catch
            {
                // Statistics logging must never affect scraping.
            }
        }

        private static void ScheduleSummary()
        {
            lock (SummaryTimerLock)
            {
                if (_summaryTimer is null)
                {
                    _summaryTimer = new Timer(_ => FlushStatistics("scrape summary"), null,
                        Timeout.Infinite, Timeout.Infinite);
                }

                _summaryTimer.Change(SummaryDelayMilliseconds, Timeout.Infinite);
            }
        }

        private static LocalMetadataConfig GetActiveConfig()
        {
            var options = GetOptions();

            if (options is null || !options.EnableLocalMetadataSource ||
                options.LocalMetadataMode == LocalMetadataMode.Off)
            {
                return null;
            }

            var root = NormalizeRoot(options.LocalMetadataRoot);

            if (root is null) return null;

            return new LocalMetadataConfig(root, options.LocalMetadataMode,
                options.LocalMetadataAutoRefreshImages);
        }

        private static PluginOptions GetOptions()
        {
            try
            {
                return Plugin.Instance?.GetPluginOptions();
            }
            catch (Exception e)
            {
                LogDebug("Local metadata source - Options read failed", e);
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
            catch (Exception e)
            {
                LogDebug("Local metadata source - Invalid metadata root: " + root, e);
                return null;
            }
        }

        private static void CreateDirectories(string root)
        {
            foreach (var baseFolder in new[] { CacheFolderName, OverrideFolderName })
            {
                foreach (var typeFolder in new[] { MoviesFolderName, TvFolderName, CollectionsFolderName })
                {
                    Directory.CreateDirectory(Path.Combine(root, baseFolder, typeFolder));
                }
            }
        }

        private static string Combine(string root, string baseFolder, string relativePath)
        {
            return Path.Combine(root, baseFolder,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static byte[] TryReadAllBytes(string path)
        {
            var gate = GetFileGate(path);
            gate.Wait();

            try
            {
                if (!File.Exists(path)) return null;

                var bytes = File.ReadAllBytes(path);

                return bytes.Length > 0 ? bytes : null;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failures);
                ScheduleSummary();
                LogDebug("Local metadata read failed - " + path, e);
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        private static HttpResponseInfo CreateResponse(LocalMetadataTarget target, byte[] bytes, string source)
        {
            return new HttpResponseInfo
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/json; charset=utf-8",
                Content = new MemoryStream(bytes),
                ContentLength = bytes.Length,
                ResponseUrl = target.RequestUrl,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-StrmAssistant-LocalMetadata"] = source
                }
            };
        }

        private static async Task<string> ReadBodyAsync(HttpResponseInfo response)
        {
            var stream = response.Content;

            byte[] bytes;

            using (var buffer = new MemoryStream())
            {
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }

                await stream.CopyToAsync(buffer).ConfigureAwait(false);
                bytes = buffer.ToArray();
            }

            try
            {
                stream.Dispose();
            }
            catch
            {
                // Best effort cleanup only.
            }

            response.Content = new MemoryStream(bytes);
            response.ContentLength = bytes.Length;

            if (bytes.Length == 0) return null;

            var body = Encoding.UTF8.GetString(bytes);

            return body.Length > 0 && body[0] == '\uFEFF' ? body.Substring(1) : body;
        }

        private static bool WriteTextFile(string path, string content)
        {
            return WriteBytesFile(path, Utf8NoBom.GetBytes(content));
        }

        private static string AttachMetadata(string body, LocalMetadataTarget target,
            LocalMetadataCaptureContext context)
        {
            if (_jsonSerializer is null || string.IsNullOrWhiteSpace(body)) return body;

            try
            {
                var document = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(body);

                if (document is null) return body;

                document["_meta"] = context ?? new LocalMetadataCaptureContext
                {
                    source = "online",
                    request = target.Kind + ":" + target.TmdbId,
                    url = target.RequestUrl
                };

                return _jsonSerializer.SerializeToString(document);
            }
            catch (Exception e)
            {
                LogDebug("Local metadata source - Metadata annotation failed", e);
                return body;
            }
        }

        private static bool WriteBytesFile(string path, byte[] bytes)
        {
            if (bytes is null || bytes.Length == 0) return false;

            var gate = GetFileGate(path);
            gate.Wait();
            string tempPath = null;

            try
            {
                var directory = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(path))
                {
                    try
                    {
                        if (File.ReadAllBytes(path).SequenceEqual(bytes)) return false;
                    }
                    catch
                    {
                        // Compare is an optimization only; continue with the atomic write.
                    }
                }

                tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tempPath, bytes);

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(tempPath, path);
                tempPath = null;

                return true;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failures);
                ScheduleSummary();
                LogDebug("Local metadata write failed - " + path, e);
                return false;
            }
            finally
            {
                gate.Release();

                if (tempPath != null)
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // Best effort cleanup only.
                    }
                }
            }
        }

        private static async Task CaptureImagesAsync(LocalMetadataConfig config, LocalMetadataTarget target,
            string body)
        {
            if (_jsonSerializer is null || _httpClient is null) return;

            LocalMetadataImageDocument document;

            try
            {
                document = _jsonSerializer.DeserializeFromString<LocalMetadataImageDocument>(body);
            }
            catch (Exception e)
            {
                LogDebug("Local metadata image scan failed - " + target.RelativePaths[0], e);
                return;
            }

            if (document is null) return;

            var images = ExtractImages(target, document);

            if (images.Count == 0) return;

            var idDirectory = Path.Combine(config.Root, CacheFolderName,
                target.RelativePaths[0].Substring(0, target.RelativePaths[0].LastIndexOf('/'))
                    .Replace('/', Path.DirectorySeparatorChar));
            var imagesDirectory = Path.Combine(idDirectory, ImagesFolderName);

            foreach (var image in images)
            {
                var path = Path.Combine(imagesDirectory, image.Type + GetImageExtension(image.FilePath));

                if (File.Exists(path)) continue;

                var url = BuildImageUrl(image.FilePath);

                if (url is null) continue;

                var bytes = await DownloadImageAsync(url).ConfigureAwait(false);

                if (bytes is null || bytes.Length == 0) continue;

                if (WriteBytesFile(path, bytes))
                {
                    Interlocked.Increment(ref _imageWrites);
                    ScheduleSummary();
                    LogDebug("Local metadata image captured - " + path);
                }
            }
        }

        private static List<LocalMetadataImage> ExtractImages(LocalMetadataTarget target,
            LocalMetadataImageDocument document)
        {
            var images = new List<LocalMetadataImage>();
            var addedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string type, string filePath)
            {
                if (string.IsNullOrWhiteSpace(filePath) || !addedTypes.Add(type)) return;

                images.Add(new LocalMetadataImage { Type = type, FilePath = filePath });
            }

            if (target.Kind == LocalMetadataTargetKind.Episode)
            {
                Add("poster", document.still_path);
                Add("poster", FirstImagePath(document.images?.stills));
                Add("poster", FirstImagePath(document.images?.posters));
                return images;
            }

            Add("poster", document.poster_path);
            Add("poster", FirstImagePath(document.images?.posters));

            if (target.Kind == LocalMetadataTargetKind.Season) return images;

            Add("fanart", document.backdrop_path);
            Add("fanart", FirstImagePath(document.images?.backdrops));
            Add("clearlogo", document.logo_path);
            Add("clearlogo", FirstImagePath(document.images?.logos));
            Add("landscape", FirstImagePath(document.images?.landscapes));
            Add("banner", FirstImagePath(document.images?.banners));
            Add("discart", FirstImagePath(document.images?.discarts));
            Add("clearart", FirstImagePath(document.images?.cleararts));

            return images;
        }

        private static string FirstImagePath(List<LocalMetadataImageEntry> entries)
        {
            return entries?.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry?.file_path))?.file_path;
        }

        private static string BuildImageUrl(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            if (filePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                filePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return filePath;
            }

            var root = MovieDbApi.GetEffectiveImageDownloadRoot();

            if (string.IsNullOrWhiteSpace(root)) return null;

            return root.TrimEnd('/') + "/original" +
                   (filePath.StartsWith("/", StringComparison.Ordinal) ? filePath : "/" + filePath);
        }

        private static string GetImageExtension(string filePath)
        {
            try
            {
                var path = filePath;

                if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri))
                {
                    path = uri.AbsolutePath;
                }

                var extension = Path.GetExtension(path).ToLowerInvariant();

                switch (extension)
                {
                    case ".jpg":
                    case ".jpeg":
                    case ".png":
                    case ".webp":
                        return extension;
                    default:
                        return ".jpg";
                }
            }
            catch
            {
                return ".jpg";
            }
        }

        private static async Task<byte[]> DownloadImageAsync(string url)
        {
            try
            {
                var options = new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = CancellationToken.None,
                    AcceptHeader = "image/*",
                    BufferContent = true,
                    UserAgent = Plugin.Instance?.UserAgent,
                    TimeoutMs = 15000,
                    ThrowOnErrorResponse = false,
                    LogRequest = false,
                    LogResponse = false,
                    LogErrors = false
                };

                using (var response = await _httpClient.GetResponse(options).ConfigureAwait(false))
                {
                    if (response?.StatusCode != HttpStatusCode.OK || response.Content is null)
                    {
                        return null;
                    }

                    using (var buffer = new MemoryStream())
                    {
                        await response.Content.CopyToAsync(buffer).ConfigureAwait(false);
                        return buffer.Length > 0 ? buffer.ToArray() : null;
                    }
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failures);
                ScheduleSummary();
                LogDebug("Local metadata image download failed - " + url, e);
                return null;
            }
        }

        private static bool IsSuccessful(HttpResponseInfo response)
        {
            if (response is null) return false;

            var statusCode = (int)response.StatusCode;
            return statusCode >= 200 && statusCode < 300;
        }

        private static SemaphoreSlim GetFileGate(string path)
        {
            var hash = 0;

            foreach (var character in path)
            {
                hash = unchecked(hash * 31 + character);
            }

            return FileLocks[(hash & int.MaxValue) % FileLocks.Length];
        }

        private static void LogDebug(string message, Exception exception = null)
        {
            try
            {
                _logger?.Debug(exception is null ? message : message + " - " + exception.Message);
            }
            catch
            {
                // Logging must never break metadata resolution.
            }
        }

        internal sealed class LocalMetadataTarget
        {
            public LocalMetadataTargetKind Kind { get; set; }
            public string TmdbId { get; set; }
            public string RequestUrl { get; set; }
            public string[] RelativePaths { get; set; }
        }

        internal enum LocalMetadataTargetKind
        {
            Movie,
            Series,
            Season,
            Episode,
            Collection
        }

        private sealed class LocalMetadataConfig
        {
            public LocalMetadataConfig(string root, LocalMetadataMode mode, bool autoRefreshImages)
            {
                Root = root;
                Mode = mode;
                AutoRefreshImages = autoRefreshImages;
            }

            public string Root { get; }
            public LocalMetadataMode Mode { get; }
            public bool AutoRefreshImages { get; }
        }

        private sealed class LocalMetadataImage
        {
            public string Type { get; set; }
            public string FilePath { get; set; }
        }

        public sealed class LocalMetadataImageDocument
        {
            public string poster_path { get; set; }
            public string backdrop_path { get; set; }
            public string logo_path { get; set; }
            public string still_path { get; set; }
            public LocalMetadataImageCollection images { get; set; }
        }

        public sealed class LocalMetadataImageCollection
        {
            public List<LocalMetadataImageEntry> posters { get; set; }
            public List<LocalMetadataImageEntry> backdrops { get; set; }
            public List<LocalMetadataImageEntry> logos { get; set; }
            public List<LocalMetadataImageEntry> stills { get; set; }
            public List<LocalMetadataImageEntry> landscapes { get; set; }
            public List<LocalMetadataImageEntry> banners { get; set; }
            public List<LocalMetadataImageEntry> discarts { get; set; }
            public List<LocalMetadataImageEntry> cleararts { get; set; }
        }

        public sealed class LocalMetadataImageEntry
        {
            public string file_path { get; set; }
        }

        public sealed class LocalMetadataCaptureContext
        {
            public string source { get; set; }
            public string request { get; set; }
            public string url { get; set; }
            public string language { get; set; }
            public bool converted { get; set; }
            public bool rewritten { get; set; }
        }
    }
}
