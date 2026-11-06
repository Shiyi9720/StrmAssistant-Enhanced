using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Distributed media info extraction. Worker nodes run the same plugin and expose a small
    /// endpoint that probes a media path with Emby's own probe manager. The master only sends the
    /// resolved media path and applies the returned streams/chapters; every transport or result
    /// failure returns false so the caller can continue with local extraction.
    /// </summary>
    public class DistributedExtractApi
    {
        private const int MaxNodeBackoffSeconds = 600;
        private const int MinRequestTimeoutSeconds = 10;

        private sealed class NodeHealth
        {
            public int Failures;
            public long BlockedUntilTicks;
        }

        // Public so that Emby's JSON serializer can construct it on the master node.
        public sealed class MediaInfoPayload
        {
            public List<MediaStream> MediaStreams { get; set; } = new List<MediaStream>();
            public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
            public long? RunTimeTicks { get; set; }
            public long? Size { get; set; }
            public int? Bitrate { get; set; }
            public string Container { get; set; }
        }

        private static readonly ConcurrentDictionary<string, NodeHealth> NodeStates =
            new ConcurrentDictionary<string, NodeHealth>(StringComparer.OrdinalIgnoreCase);

        private static int _roundRobinIndex;

        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IMediaProbeManager _mediaProbeManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        public DistributedExtractApi(IHttpClient httpClient, IJsonSerializer jsonSerializer,
            IMediaProbeManager mediaProbeManager, ILibraryManager libraryManager, IItemRepository itemRepository)
        {
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
            _mediaProbeManager = mediaProbeManager;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _logger = Plugin.Instance?.Logger;
        }

        private static readonly DistributedExtractOptions DefaultOptions = new DistributedExtractOptions();

        private static DistributedExtractOptions Options =>
            Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions?.DistributedExtractOptions ??
            DefaultOptions;

        private static bool IsEnabled
        {
            get
            {
                var options = Options;

                return options.EnableDistributedExtract &&
                       !string.IsNullOrWhiteSpace(options.SharedApiKey) &&
                       !string.IsNullOrWhiteSpace(options.WorkerNodes);
            }
        }

        /// <summary>
        /// Tries the configured worker nodes in turn and applies the first usable result.
        /// Returns false immediately when the feature is disabled and on every failure.
        /// </summary>
        public async Task<bool> TryExtractAsync(BaseItem item, string mediaPath, string source,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!IsEnabled || !(item is Video || item is Audio)) return false;

                if (string.IsNullOrWhiteSpace(mediaPath) || !IsDistributable(item, mediaPath)) return false;

                var nodes = GetWorkerNodes();

                if (nodes.Count == 0) return false;

                var options = Options;
                var timeoutSeconds = Math.Max(MinRequestTimeoutSeconds, options.TimeoutSeconds);
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                var startIndex = (int)((uint)Interlocked.Increment(ref _roundRobinIndex) % (uint)nodes.Count);

                for (var i = 0; i < nodes.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var now = DateTime.UtcNow;

                    if (now >= deadline) break;

                    var node = nodes[(startIndex + i) % nodes.Count];
                    var health = NodeStates.GetOrAdd(node, _ => new NodeHealth());

                    if (Interlocked.Read(ref health.BlockedUntilTicks) > now.Ticks)
                    {
                        _logger?.Debug("DistributedExtract - Node temporarily unavailable: " + node);
                        continue;
                    }

                    var remaining = deadline - now;
                    var mappedPath = MapPath(mediaPath, options.PathMapping);
                    var request = new DistributedMediaInfoRequest
                    {
                        ApiKey = options.SharedApiKey,
                        Path = mappedPath,
                        Protocol = GetProtocol(mappedPath).ToString(),
                        Container = Path.GetExtension(mediaPath).TrimStart('.'),
                        Name = item.Name,
                        MediaType = item is Audio ? "Audio" : "Video",
                        ExtractChapters = true,
                        TimeoutSeconds = Math.Max(MinRequestTimeoutSeconds, (int)remaining.TotalSeconds)
                    };

                    DistributedMediaInfoResponse response;

                    try
                    {
                        response = await SendAsync(node, request, remaining, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        RecordNodeFailure(node, health, "timeout");
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        RecordNodeFailure(node, health, e.Message);
                        continue;
                    }

                    if (response?.Success != true)
                    {
                        // A worker side rejection is usually about this file (missing path,
                        // nothing probed), so the node itself is still considered healthy.
                        _logger?.Debug("DistributedExtract - No usable result from " + node + ": " +
                                       (response?.Error ?? "empty response") + " - " + item.Path);
                        continue;
                    }

                    bool applied;

                    try
                    {
                        applied = TryApply(item, response, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        _logger?.Info("DistributedExtract - Apply failed: " + item.Path + " - " + e.Message);
                        _logger?.Debug(e.StackTrace);
                        return false;
                    }

                    if (!applied)
                    {
                        _logger?.Debug("DistributedExtract - Unusable payload from " + node + ": " + item.Path);
                        continue;
                    }

                    Interlocked.Exchange(ref health.Failures, 0);
                    Interlocked.Exchange(ref health.BlockedUntilTicks, 0);

                    _logger?.Info("DistributedExtract - Extracted via " + node + " (" + source +
                                  "): " + item.Name + " - " + item.Path);

                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger?.Debug("DistributedExtract - Failed: " + item?.Path + " - " + e.Message);
                _logger?.Debug(e.StackTrace);
            }

            return false;
        }

        /// <summary>
        /// Worker side: validates the shared key and probes the requested path.
        /// </summary>
        public async Task<DistributedMediaInfoResponse> HandleWorkerRequestAsync(
            DistributedMediaInfoRequest request, CancellationToken cancellationToken)
        {
            var options = Options;

            if (!options.EnableDistributedExtract || string.IsNullOrWhiteSpace(options.SharedApiKey))
            {
                return Fail("Distributed extraction is not enabled on this node");
            }

            if (!SecretEquals(request?.ApiKey, options.SharedApiKey))
            {
                _logger?.Info("DistributedExtract - Worker request rejected: invalid API key");
                return Fail("Invalid API key");
            }

            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return Fail("Path is required");
            }

            var protocol = ParseProtocol(request.Protocol, request.Path);

            if (protocol == MediaProtocol.File && !File.Exists(request.Path))
            {
                return Fail("File not found: " + request.Path);
            }

            if (protocol != MediaProtocol.File && !IsHttpProtocol(protocol))
            {
                return Fail("Unsupported protocol: " + protocol);
            }

            var timeoutSeconds = request.TimeoutSeconds > 0
                ? Math.Max(MinRequestTimeoutSeconds, Math.Min(600, request.TimeoutSeconds))
                : Math.Max(MinRequestTimeoutSeconds, options.TimeoutSeconds);

            var started = DateTime.UtcNow;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var mediaSource = new MediaSourceInfo
                {
                    Path = request.Path,
                    Protocol = protocol,
                    Container = request.Container,
                    Name = request.Name,
                    IsRemote = protocol != MediaProtocol.File
                };

                var mediaInfo = await _mediaProbeManager.GetMediaInfo(new MediaInfoRequest
                {
                    MediaSource = mediaSource,
                    ExtractChapters = request.ExtractChapters,
                    MediaType = string.Equals(request.MediaType, "Audio", StringComparison.OrdinalIgnoreCase)
                        ? DlnaProfileType.Audio
                        : DlnaProfileType.Video
                }, cts.Token).ConfigureAwait(false);

                if (mediaInfo is null)
                {
                    return Fail("Probe returned no result");
                }

                var streams = mediaInfo.MediaStreams?
                    .Where(s => (s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio ||
                                 s.Type == MediaStreamType.Subtitle) &&
                                !(s.IsExternal && s.Type == MediaStreamType.Subtitle))
                    .ToList() ?? new List<MediaStream>();

                if (streams.Count == 0 ||
                    streams.All(s => s.Type != MediaStreamType.Video && s.Type != MediaStreamType.Audio))
                {
                    return Fail("No media streams found");
                }

                for (var i = 0; i < streams.Count; i++)
                {
                    streams[i].Index = i;
                }

                var chapters = (mediaInfo.Chapters ?? Array.Empty<ChapterInfo>())
                    .Select(c =>
                    {
                        c.ImageTag = null;
                        return c;
                    })
                    .ToList();

                var payload = new MediaInfoPayload
                {
                    MediaStreams = streams,
                    Chapters = chapters,
                    RunTimeTicks = mediaInfo.RunTimeTicks,
                    Size = mediaInfo.Size,
                    Bitrate = mediaInfo.Bitrate,
                    Container = mediaInfo.Container
                };

                _logger?.Debug("DistributedExtract - Worker probed " + request.Path + " -> " +
                               streams.Count + " streams, " + chapters.Count + " chapters");

                return new DistributedMediaInfoResponse
                {
                    Success = true,
                    MediaInfoJson = _jsonSerializer.SerializeToString(payload),
                    NodeName = Plugin.Instance?.Name,
                    ElapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds
                };
            }
            catch (OperationCanceledException)
            {
                return Fail("Probe timed out after " + timeoutSeconds + " seconds");
            }
            catch (Exception e)
            {
                _logger?.Info("DistributedExtract - Worker probe failed: " + request.Path + " - " + e.Message);
                _logger?.Debug(e.StackTrace);
                return Fail(e.Message);
            }
        }

        private async Task<DistributedMediaInfoResponse> SendAsync(string node,
            DistributedMediaInfoRequest request, TimeSpan budget, CancellationToken cancellationToken)
        {
            var timeoutMs = Math.Max(1000, (int)Math.Min(int.MaxValue, budget.TotalMilliseconds));

            var httpOptions = new HttpRequestOptions
            {
                Url = node + "/StrmAssistant/DistributedExtract/MediaInfo",
                RequestContent = _jsonSerializer.SerializeToString(request).AsMemory(),
                RequestContentType = "application/json",
                AcceptHeader = "application/json",
                TimeoutMs = timeoutMs,
                ThrowOnErrorResponse = false,
                LogRequest = false,
                LogResponse = false,
                LogErrors = false,
                CancellationToken = cancellationToken,
                UserAgent = Plugin.Instance?.UserAgent
            };

            using var response = await _httpClient.SendAsync(httpOptions, "POST").ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException("HTTP " + (int)response.StatusCode + " from " + node);
            }

            using var reader = new StreamReader(response.Content, Encoding.UTF8);

            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body)) return null;

            return _jsonSerializer.DeserializeFromString<DistributedMediaInfoResponse>(body);
        }

        private bool TryApply(BaseItem item, DistributedMediaInfoResponse response,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(response.MediaInfoJson)) return false;

            MediaInfoPayload payload;

            try
            {
                payload = _jsonSerializer.DeserializeFromString<MediaInfoPayload>(response.MediaInfoJson);
            }
            catch (Exception e)
            {
                _logger?.Debug("DistributedExtract - Invalid payload: " + e.Message);
                return false;
            }

            var streams = payload?.MediaStreams?
                .Where(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio ||
                            s.Type == MediaStreamType.Subtitle)
                .ToList();

            if (streams is null || streams.Count == 0 ||
                streams.All(s => s.Type != MediaStreamType.Video && s.Type != MediaStreamType.Audio))
            {
                return false;
            }

            for (var i = 0; i < streams.Count; i++)
            {
                streams[i].Index = i;
            }

            _itemRepository.SaveMediaStreams(item.InternalId, streams, cancellationToken);
            item.MediaStreams = streams;

            if (payload.RunTimeTicks > 0) item.RunTimeTicks = payload.RunTimeTicks;
            if (payload.Size > 0) item.Size = payload.Size.Value;
            if (payload.Bitrate > 0) item.TotalBitrate = payload.Bitrate.Value;

            if (string.IsNullOrEmpty(item.Container) && !string.IsNullOrWhiteSpace(payload.Container))
            {
                item.Container = payload.Container;
            }

            var videoStream = streams
                .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                .OrderByDescending(s => (long)s.Width.Value * s.Height.Value)
                .FirstOrDefault();

            if (videoStream != null)
            {
                item.Width = videoStream.Width.GetValueOrDefault();
                item.Height = videoStream.Height.GetValueOrDefault();
            }

            SaveChapters(item, payload.Chapters);

            item.DateLastRefreshed = new DateTimeOffset();

            _libraryManager.UpdateItems(new List<BaseItem> { item }, null, ItemUpdateType.MetadataImport,
                false, false, null, cancellationToken);

            return true;
        }

        private void SaveChapters(BaseItem item, List<ChapterInfo> chapters)
        {
            if (chapters is null || chapters.Count == 0) return;

            // Intro/credits markers written by the intro skip feature must survive a re-probe.
            var markers = _itemRepository.GetChapters(item)?
                .Where(c => c.MarkerType != MarkerType.Chapter)
                .ToList() ?? new List<ChapterInfo>();

            var merged = markers.Concat(chapters)
                .GroupBy(c => c.StartPositionTicks)
                .Select(g => g.First())
                .OrderBy(c => c.StartPositionTicks)
                .ToList();

            for (var i = 0; i < merged.Count; i++)
            {
                merged[i].ChapterIndex = i;
                merged[i].ImageTag = null;
            }

            _itemRepository.SaveChapters(item.InternalId, true, merged);
        }

        private void RecordNodeFailure(string node, NodeHealth health, string reason)
        {
            var failures = Interlocked.Increment(ref health.Failures);
            var retrySeconds = 0;

            if (failures >= 2)
            {
                retrySeconds = (int)Math.Min(MaxNodeBackoffSeconds,
                    30 * Math.Pow(2, Math.Min(failures - 2, 4)));
                Interlocked.Exchange(ref health.BlockedUntilTicks,
                    DateTime.UtcNow.AddSeconds(retrySeconds).Ticks);
            }

            var message = "DistributedExtract - Node unavailable: " + node + " - " + reason;

            if (failures == 1)
            {
                _logger?.Info(message);
            }
            else
            {
                _logger?.Debug(message + " (retry in " + retrySeconds + "s)");
            }
        }

        private static List<string> GetWorkerNodes()
        {
            var raw = Options.WorkerNodes;

            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

            return raw.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().TrimEnd('/'))
                .Where(s => Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsDistributable(BaseItem item, string path)
        {
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            if (extension == "iso" || extension == "img") return false;

            var container = item.MediaContainer;

            if (container == MediaContainers.Bluray || container == MediaContainers.Dvd) return false;

            // Folder based discs cannot be probed by ffprobe.
            return !(Directory.Exists(path) && !File.Exists(path));
        }

        private static MediaProtocol GetProtocol(string path)
        {
            // Emby represents both http and https sources with MediaProtocol.Http.
            return path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? MediaProtocol.Http
                : MediaProtocol.File;
        }

        private static MediaProtocol ParseProtocol(string value, string path)
        {
            return Enum.TryParse<MediaProtocol>(value, true, out var protocol) &&
                   Enum.IsDefined(typeof(MediaProtocol), protocol)
                ? protocol
                : GetProtocol(path);
        }

        private static bool IsHttpProtocol(MediaProtocol protocol)
        {
            return protocol == MediaProtocol.Http;
        }

        private static string MapPath(string path, string mapping)
        {
            if (string.IsNullOrWhiteSpace(mapping) || string.IsNullOrEmpty(path)) return path;

            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            foreach (var entry in mapping.Split(new[] { ',', ';', '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var index = entry.IndexOf("=>", StringComparison.Ordinal);

                if (index <= 0) continue;

                var from = entry.Substring(0, index).Trim().TrimEnd('/', '\\');
                var to = entry.Substring(index + 2).Trim().TrimEnd('/', '\\');

                if (from.Length == 0) continue;

                if (path.Equals(from, comparison) ||
                    path.StartsWith(from + "/", comparison) ||
                    path.StartsWith(from + "\\", comparison))
                {
                    var remainder = path.Substring(from.Length);

                    return string.IsNullOrEmpty(to) ? remainder : to + remainder;
                }
            }

            return path;
        }

        private static bool SecretEquals(string provided, string expected)
        {
            if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;

            var providedBytes = Encoding.UTF8.GetBytes(provided);
            var expectedBytes = Encoding.UTF8.GetBytes(expected);

            return providedBytes.Length == expectedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
        }

        private static DistributedMediaInfoResponse Fail(string error)
        {
            return new DistributedMediaInfoResponse { Success = false, Error = error };
        }
    }
}
