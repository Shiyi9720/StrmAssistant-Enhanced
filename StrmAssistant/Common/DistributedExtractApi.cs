using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
    /// Distributed extraction. Worker nodes run the same plugin and expose small endpoints that
    /// probe media, extract chapter images and create audio fingerprints with Emby's own
    /// components. The master only sends resolved paths and applies the returned data; every
    /// transport or result failure returns false so the caller can continue with local extraction.
    /// </summary>
    public class DistributedExtractApi
    {
        private const int MaxNodeBackoffSeconds = 600;
        private const int MinRequestTimeoutSeconds = 10;
        private const int FingerprintSampleRate = 16000;
        private const int MaxDvP5FrameCount = 100;
        private const int MaxDvP5FrameBytes = 20 * 1024 * 1024;
        private static readonly TimeSpan MediaRootsCacheDuration = TimeSpan.FromSeconds(300);
        private static readonly StringComparison PathComparison =
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static readonly long FirstChapterTicks = TimeSpan.FromSeconds(15).Ticks;

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
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> MediaInfoDistributionGates =
            new ConcurrentDictionary<int, SemaphoreSlim>();

        private static int _roundRobinIndex;

        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IMediaProbeManager _mediaProbeManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IImageExtractionManager _imageExtractionManager;
        private readonly IFfmpegManager _ffmpegManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IApplicationPaths _applicationPaths;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        private readonly object _mediaRootsLock = new object();
        private List<string> _mediaRootsCache;
        private DateTime _mediaRootsCacheUtc = DateTime.MinValue;

        public DistributedExtractApi(IHttpClient httpClient, IJsonSerializer jsonSerializer,
            IMediaProbeManager mediaProbeManager, ILibraryManager libraryManager, IItemRepository itemRepository,
            IImageExtractionManager imageExtractionManager, IFfmpegManager ffmpegManager,
            IMediaEncoder mediaEncoder, IApplicationPaths applicationPaths, IFileSystem fileSystem,
            IServerApplicationHost serverApplicationHost)
        {
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
            _mediaProbeManager = mediaProbeManager;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _imageExtractionManager = imageExtractionManager is null
                ? null
                : ImageExtractionTimeout.Wrap(imageExtractionManager, ffmpegManager, serverApplicationHost);
            _ffmpegManager = ffmpegManager;
            _mediaEncoder = mediaEncoder;
            _applicationPaths = applicationPaths;
            _fileSystem = fileSystem;
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

        public static bool IsThumbnailDistributionEnabled => IsThumbnailEnabled;

        public static bool IsMediaInfoDistributionEnabled => IsMediaInfoEnabled;

        public static bool IsFingerprintDistributionEnabled => IsFingerprintEnabled;

        public static bool IsHdrVividDistributionEnabled => IsHdrVividEnabled;

        public static bool IsDvP5DistributionEnabled => IsDvP5Enabled;

        private static bool IsThumbnailEnabled => IsEnabled && Options.EnableDistributedThumbnail;

        private static bool IsMediaInfoEnabled => IsEnabled && Options.EnableDistributedMediaInfo;

        private static bool IsFingerprintEnabled => IsEnabled && Options.EnableDistributedFingerprint;

        private static bool IsHdrVividEnabled => IsEnabled && Options.EnableDistributedHdrVivid;

        private static bool IsDvP5Enabled => IsEnabled && Options.EnableDistributedDvP5;

        /// <summary>
        /// Tries the configured worker nodes in turn and applies the first usable result.
        /// Returns false immediately when the feature is disabled and on every failure.
        /// </summary>
        public async Task<bool> TryExtractAsync(BaseItem item, string mediaPath, string source,
            CancellationToken cancellationToken)
        {
            var attempt = await TryExtractWithResultAsync(item, mediaPath, source, cancellationToken)
                .ConfigureAwait(false);

            return attempt.Success;
        }

        /// <summary>
        /// Master side of distributed media-info extraction. The worker returns raw ffprobe JSON;
        /// the master normalizes it and writes the result through the existing media-info apply
        /// path. Every failure is returned as a reason so the task can report local/worker fallback
        /// counts without changing the caller's local fallback behaviour.
        /// </summary>
        public async Task<MediaInfoDistributionAttempt> TryExtractWithResultAsync(BaseItem item, string mediaPath,
            string source, CancellationToken cancellationToken)
        {
            if (!Options.EnableDistributedMediaInfo)
            {
                return new MediaInfoDistributionAttempt();
            }

            if (!(item is Video || item is Audio))
            {
                return new MediaInfoDistributionAttempt();
            }

            if (string.IsNullOrWhiteSpace(mediaPath) || !IsDistributable(item, mediaPath) ||
                !IsLocalDistributablePath(mediaPath))
            {
                return MediaInfoFallback(item, MediaInfoFallbackReason.PathNotAllowed);
            }

            if (!IsMediaInfoEnabled)
            {
                return MediaInfoFallback(item, MediaInfoFallbackReason.NoWorker);
            }

            var concurrency = Options.MediaInfoWorkerConcurrency;
            concurrency = concurrency <= 0 ? 2 : Math.Min(4, concurrency);

            var gate = MediaInfoDistributionGates.GetOrAdd(concurrency,
                value => new SemaphoreSlim(value, value));

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var attempt = await TryWorkersWithResultAsync<DistributedMediaInfoRequest, DistributedMediaInfoResponse>(
                    source, item.Name + " - " + item.Path, "/StrmAssistant/DistributedExtract/MediaInfo",
                    remaining =>
                    {
                        var options = Options;
                        var mappedPath = MapPath(mediaPath, options.PathMapping);

                        return new DistributedMediaInfoRequest
                        {
                            ApiKey = options.SharedApiKey,
                            Path = mappedPath,
                            TimeoutSeconds = Math.Max(MinRequestTimeoutSeconds,
                                Math.Min(600, (int)remaining.TotalSeconds))
                        };
                    },
                    response => response?.Success == true && !string.IsNullOrWhiteSpace(response.FfprobeJson),
                    response => response?.Error,
                    response => response?.ErrorCode,
                    response => TryApply(item, response, mediaPath, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (!attempt.Success && attempt.Attempted)
                {
                    LogMediaInfoFallback(item, attempt.FallbackReason);
                }

                return attempt;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Master side of distributed chapter image extraction. Only the chapter images that are
        /// still missing are extracted by a worker; the returned JPEG frames are written to the
        /// same internal metadata paths Emby uses and the chapters are saved. Returns false on any
        /// failure so the caller continues with the local extraction.
        /// </summary>
        public async Task<bool> TryExtractChapterImagesAsync(Video item, string mediaPath,
            List<ChapterInfo> chapters, CancellationToken cancellationToken)
        {
            if (!IsThumbnailEnabled || _imageExtractionManager is null) return false;

            if (item is null || chapters is null || chapters.Count == 0) return false;

            if (string.IsNullOrWhiteSpace(mediaPath) || !IsDistributable(item, mediaPath)) return false;

            var runtimeTicks = item.RunTimeTicks.GetValueOrDefault();
            var points = new List<DistributedThumbnailPoint>();

            foreach (var chapter in chapters)
            {
                if (chapter.StartPositionTicks >= runtimeTicks) break;

                var existingPath = GetChapterImagePath(item, chapter.StartPositionTicks);

                if (File.Exists(existingPath)) continue;

                // Emby extracts the first chapter at 15 seconds to avoid black frames.
                var offsetTicks = chapter.StartPositionTicks <= 0
                    ? Math.Min(FirstChapterTicks, runtimeTicks)
                    : chapter.StartPositionTicks;

                points.Add(new DistributedThumbnailPoint
                {
                    StartPositionTicks = chapter.StartPositionTicks,
                    OffsetSeconds = TimeSpan.FromTicks(Math.Max(0, offsetTicks)).TotalSeconds
                });
            }

            if (points.Count == 0) return false;

            return await TryWorkersAsync<DistributedThumbnailRequest, DistributedThumbnailResponse>(
                "VideoThumbnail", item.Name + " - " + item.Path, "/StrmAssistant/DistributedExtract/Thumbnail",
                remaining =>
                {
                    var options = Options;
                    var mappedPath = MapPath(mediaPath, options.PathMapping);

                    return new DistributedThumbnailRequest
                    {
                        ApiKey = options.SharedApiKey,
                        Path = mappedPath,
                        Protocol = GetProtocol(mappedPath).ToString(),
                        Container = Path.GetExtension(mediaPath).TrimStart('.'),
                        Name = item.Name,
                        Points = points,
                        TimeoutSeconds = Math.Max(MinRequestTimeoutSeconds, (int)remaining.TotalSeconds)
                    };
                },
                response => response?.Success == true && response.Images != null,
                response => response?.Error,
                response => TryApplyChapterImages(item, points, chapters, response),
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Master side of distributed intro fingerprint extraction. Returns the path of the
        /// fingerprint file that was written, or null on any failure (the caller then runs the
        /// local extraction). Emby's own pipeline reads the same .fp file afterwards.
        /// </summary>
        public async Task<string> TryCreateTitleFingerprintAsync(Episode item, LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            if (!IsFingerprintEnabled || _ffmpegManager is null || _mediaEncoder is null) return null;

            if (item is null) return null;

            var mediaPath = item.Path;

            if (item.IsShortcut)
            {
                try
                {
                    mediaPath = await Plugin.LibraryApi.GetStrmMountPath(mediaPath).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger?.Debug("DistributedExtract - Failed to resolve shortcut: " + item.Path + " - " +
                                   e.Message);
                    return null;
                }
            }

            if (string.IsNullOrWhiteSpace(mediaPath) || !IsDistributable(item, mediaPath)) return null;

            var minutes = GetTitleFingerprintDurationMinutes(libraryOptions);

            if (item.RunTimeTicks.GetValueOrDefault() < TimeSpan.FromMinutes(minutes).Ticks) return null;

            var targetPath = GetTitleFingerprintPath(item, minutes);

            if (File.Exists(targetPath)) return null;

            var mappedMediaPath = mediaPath;

            var success = await TryWorkersAsync<DistributedFingerprintRequest, DistributedFingerprintResponse>(
                "IntroFingerprint", item.Name + " - " + item.Path, "/StrmAssistant/DistributedExtract/Fingerprint",
                remaining =>
                {
                    var options = Options;
                    var mappedPath = MapPath(mappedMediaPath, options.PathMapping);

                    return new DistributedFingerprintRequest
                    {
                        ApiKey = options.SharedApiKey,
                        Path = mappedPath,
                        Protocol = GetProtocol(mappedPath).ToString(),
                        Name = item.Name,
                        Minutes = minutes,
                        TimeoutSeconds = Math.Max(MinRequestTimeoutSeconds, (int)remaining.TotalSeconds)
                    };
                },
                response => response?.Success == true && !string.IsNullOrWhiteSpace(response.Data),
                response => response?.Error,
                response => TryApplyTitleFingerprint(targetPath, response),
                cancellationToken).ConfigureAwait(false);

            return success ? targetPath : null;
        }

        /// <summary>
        /// Master side of distributed HDR Vivid detection. Returns true/false when a worker
        /// produced a usable answer, or null when the caller should probe locally.
        /// </summary>
        public async Task<bool?> TryDetectHdrVividAsync(string mediaPath, CancellationToken cancellationToken)
        {
            if (!IsHdrVividEnabled) return null;

            if (!IsLocalDistributablePath(mediaPath)) return null;

            bool? detected = null;

            var handled = await TryWorkersAsync<DistributedHdrVividRequest, DistributedHdrVividResponse>(
                "HdrVivid", mediaPath, "/StrmAssistant/DistributedExtract/HdrVivid",
                remaining =>
                {
                    var options = Options;
                    var mappedPath = MapPath(mediaPath, options.PathMapping);

                    return new DistributedHdrVividRequest
                    {
                        ApiKey = options.SharedApiKey,
                        Path = mappedPath,
                        Protocol = GetProtocol(mappedPath).ToString(),
                        TimeoutSeconds = Math.Max(MinRequestTimeoutSeconds, (int)remaining.TotalSeconds)
                    };
                },
                response => response?.Success == true,
                response => response?.Error,
                response =>
                {
                    detected = response.Detected;
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            return handled ? detected : (bool?)null;
        }

        /// <summary>
        /// Master side of distributed Dolby Vision Profile 5 chapter image extraction. Returns
        /// the decoded frames keyed by chapter start position, or null when the caller should
        /// extract locally.
        /// </summary>
        public async Task<Dictionary<long, byte[]>> TryExtractDvP5ChapterImagesAsync(string mediaPath,
            List<DistributedDvP5Point> points, CancellationToken cancellationToken)
        {
            if (!IsDvP5Enabled) return null;

            if (!IsLocalDistributablePath(mediaPath)) return null;

            if (points is null || points.Count == 0 || points.Count > MaxDvP5FrameCount) return null;

            if (points.Any(p => p is null) ||
                points.Select(p => p.StartPositionTicks).Distinct().Count() != points.Count)
            {
                return null;
            }

            Dictionary<long, byte[]> frames = null;

            var handled = await TryWorkersAsync<DistributedDvP5Request, DistributedDvP5Response>(
                "DvP5", mediaPath, "/StrmAssistant/DistributedExtract/DvP5",
                remaining =>
                {
                    var options = Options;
                    var mappedPath = MapPath(mediaPath, options.PathMapping);

                    return new DistributedDvP5Request
                    {
                        ApiKey = options.SharedApiKey,
                        Path = mappedPath,
                        Protocol = GetProtocol(mappedPath).ToString(),
                        Points = points,
                        TimeoutSeconds = Math.Max(MinRequestTimeoutSeconds, (int)remaining.TotalSeconds)
                    };
                },
                response => response?.Success == true && response.Images != null &&
                            response.Images.Count == points.Count,
                response => response?.Error,
                response =>
                {
                    var decoded = DecodeDvP5Images(points, response.Images);

                    if (decoded is null) return false;

                    frames = decoded;
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            return handled ? frames : null;
        }

        private static Dictionary<long, byte[]> DecodeDvP5Images(List<DistributedDvP5Point> points,
            List<DistributedDvP5Image> images)
        {
            if (images is null || images.Count != points.Count) return null;

            var frames = new Dictionary<long, byte[]>();

            foreach (var point in points)
            {
                var image = images.FirstOrDefault(i =>
                    i != null && i.StartPositionTicks == point.StartPositionTicks);

                if (image is null || string.IsNullOrEmpty(image.Data)) return null;

                byte[] bytes;

                try
                {
                    bytes = Convert.FromBase64String(image.Data);
                }
                catch (FormatException)
                {
                    return null;
                }

                if (bytes.Length < 4 || bytes.Length > MaxDvP5FrameBytes) return null;

                // The worker only ever returns JPEG frames. Reject anything else so a broken
                // response cannot replace a chapter image with arbitrary bytes.
                if (bytes[0] != 0xFF || bytes[1] != 0xD8) return null;

                frames[point.StartPositionTicks] = bytes;
            }

            return frames.Count == points.Count ? frames : null;
        }

        private async Task<bool> TryWorkersAsync<TRequest, TResponse>(string source, string itemDescription,
            string endpoint, Func<TimeSpan, TRequest> requestFactory, Func<TResponse, bool> isSuccess,
            Func<TResponse, string> getError, Func<TResponse, bool> apply, CancellationToken cancellationToken)
            where TResponse : class
        {
            var result = await TryWorkersWithResultAsync(source, itemDescription, endpoint, requestFactory, isSuccess,
                    getError, null, apply, cancellationToken)
                .ConfigureAwait(false);

            return result.Success;
        }

        private async Task<MediaInfoDistributionAttempt> TryWorkersWithResultAsync<TRequest, TResponse>(
            string source, string itemDescription, string endpoint, Func<TimeSpan, TRequest> requestFactory,
            Func<TResponse, bool> isSuccess, Func<TResponse, string> getError,
            Func<TResponse, string> getErrorCode, Func<TResponse, bool> apply, CancellationToken cancellationToken)
            where TResponse : class
        {
            var sawTimeout = false;
            var sawWorkerError = false;
            var sawPathNotAllowed = false;

            try
            {
                if (!IsEnabled)
                {
                    return new MediaInfoDistributionAttempt
                    {
                        FallbackReason = MediaInfoFallbackReason.NoWorker
                    };
                }

                var nodes = GetWorkerNodes();

                if (nodes.Count == 0)
                {
                    return new MediaInfoDistributionAttempt
                    {
                        Attempted = true,
                        FallbackReason = MediaInfoFallbackReason.NoWorker
                    };
                }

                var options = Options;
                var timeoutSeconds = Math.Max(MinRequestTimeoutSeconds, Math.Min(600, options.TimeoutSeconds));
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
                    var request = requestFactory(remaining);

                    TResponse response;

                    try
                    {
                        response = await SendAsync<TRequest, TResponse>(node, endpoint, request, remaining,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        sawTimeout = true;
                        RecordNodeFailure(node, health, "timeout");
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        sawWorkerError = true;
                        RecordNodeFailure(node, health, e.Message);
                        continue;
                    }

                    if (response is null || !isSuccess(response))
                    {
                        var errorCode = getErrorCode?.Invoke(response);

                        if (string.Equals(errorCode, "path_not_allowed", StringComparison.OrdinalIgnoreCase))
                        {
                            sawPathNotAllowed = true;
                        }
                        else if (string.Equals(errorCode, "timeout", StringComparison.OrdinalIgnoreCase))
                        {
                            sawTimeout = true;
                        }
                        else
                        {
                            sawWorkerError = true;
                        }

                        // A worker side rejection is usually about this file (missing path,
                        // nothing probed), so the node itself is still considered healthy.
                        _logger?.Debug("DistributedExtract - No usable result from " + node + ": " +
                                       (getError(response) ?? "empty response") + " - " + itemDescription);
                        continue;
                    }

                    bool applied;

                    try
                    {
                        applied = apply(response);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        _logger?.Info("DistributedExtract - Apply failed: " + itemDescription + " - " + e.Message);
                        _logger?.Debug(e.StackTrace);
                        return new MediaInfoDistributionAttempt
                        {
                            Attempted = true,
                            FallbackReason = MediaInfoFallbackReason.WorkerError
                        };
                    }

                    if (!applied)
                    {
                        sawWorkerError = true;
                        _logger?.Debug("DistributedExtract - Unusable payload from " + node + ": " + itemDescription);
                        continue;
                    }

                    Interlocked.Exchange(ref health.Failures, 0);
                    Interlocked.Exchange(ref health.BlockedUntilTicks, 0);

                    _logger?.Info("DistributedExtract - Extracted via " + node + " (" + source +
                                  "): " + itemDescription);

                    return new MediaInfoDistributionAttempt
                    {
                        Success = true,
                        Attempted = true
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                sawWorkerError = true;
                _logger?.Debug("DistributedExtract - Failed: " + itemDescription + " - " + e.Message);
                _logger?.Debug(e.StackTrace);
            }

            return new MediaInfoDistributionAttempt
            {
                Attempted = true,
                FallbackReason = sawPathNotAllowed
                    ? MediaInfoFallbackReason.PathNotAllowed
                    : sawTimeout
                        ? MediaInfoFallbackReason.Timeout
                        : sawWorkerError
                            ? MediaInfoFallbackReason.WorkerError
                            : MediaInfoFallbackReason.NoWorker
            };
        }

        /// <summary>
        /// Worker side: validates the shared key, confines the request to a local media file under
        /// one of this node's library roots, and returns the static ffprobe JSON unchanged.
        /// </summary>
        public async Task<DistributedMediaInfoResponse> HandleWorkerRequestAsync(
            DistributedMediaInfoRequest request, CancellationToken cancellationToken)
        {
            var options = Options;

            if (!options.EnableDistributedExtract || string.IsNullOrWhiteSpace(options.SharedApiKey))
            {
                return Fail("Distributed extraction is not enabled on this node");
            }

            if (!options.EnableDistributedMediaInfo)
            {
                return Fail("Distributed media info extraction is not enabled on this node");
            }

            if (!SecretEquals(request?.ApiKey, options.SharedApiKey))
            {
                _logger?.Info("DistributedExtract - Worker request rejected: invalid API key");
                return Fail("Invalid API key");
            }

            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return Fail("Path is required", "path_not_allowed");
            }

            if (request.Path.IndexOf('"') >= 0 || request.Path.IndexOf('\r') >= 0 ||
                request.Path.IndexOf('\n') >= 0 || request.Path.IndexOf('\0') >= 0)
            {
                return Fail("Invalid path", "path_not_allowed");
            }

            if (IsRejectedMediaPath(request.Path) ||
                !TryNormalizeAllowedMediaPath(request.Path, out var fullPath) ||
                Directory.Exists(fullPath) || IsDiscFolderPath(fullPath))
            {
                return Fail("Path is outside the media library or not a supported file: " + request.Path,
                    "path_not_allowed");
            }

            if (!File.Exists(fullPath))
            {
                return Fail("File not found: " + request.Path, "path_not_allowed");
            }

            var timeoutSeconds = request.TimeoutSeconds > 0
                ? Math.Max(MinRequestTimeoutSeconds, Math.Min(600, request.TimeoutSeconds))
                : Math.Max(MinRequestTimeoutSeconds, Math.Min(600, options.TimeoutSeconds));

            var started = DateTime.UtcNow;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var ffprobeJson = await CustomFfprobeApi.RunProbeJsonAsync(fullPath,
                        "-show_streams -show_chapters -show_format", timeoutSeconds * 1000, cts.Token)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(ffprobeJson))
                {
                    var elapsed = DateTime.UtcNow - started;

                    if (!cancellationToken.IsCancellationRequested &&
                        (cts.IsCancellationRequested || elapsed >= TimeSpan.FromSeconds(timeoutSeconds)))
                    {
                        return Fail("Probe timed out after " + timeoutSeconds + " seconds", "timeout");
                    }

                    return Fail("Probe returned no result");
                }

                _logger?.Debug("DistributedExtract - Worker probed " + fullPath);

                return new DistributedMediaInfoResponse
                {
                    Success = true,
                    FfprobeJson = ffprobeJson,
                    NodeName = Plugin.Instance?.Name,
                    ElapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds
                };
            }
            catch (OperationCanceledException)
            {
                return Fail("Probe timed out after " + timeoutSeconds + " seconds", "timeout");
            }
            catch (Exception e)
            {
                _logger?.Info("DistributedExtract - Worker probe failed: " + request.Path + " - " + e.Message);
                _logger?.Debug(e.StackTrace);
                return Fail(e.Message);
            }
        }

        /// <summary>
        /// Worker side: validates the shared key, probes the path and extracts the requested
        /// frames with Emby's image extraction manager.
        /// </summary>
        public async Task<DistributedThumbnailResponse> HandleWorkerThumbnailRequestAsync(
            DistributedThumbnailRequest request, CancellationToken cancellationToken)
        {
            var options = Options;

            if (!options.EnableDistributedExtract || string.IsNullOrWhiteSpace(options.SharedApiKey))
            {
                return FailThumbnail("Distributed extraction is not enabled on this node");
            }

            if (!options.EnableDistributedThumbnail)
            {
                return FailThumbnail("Distributed thumbnail extraction is not enabled on this node");
            }

            if (!SecretEquals(request?.ApiKey, options.SharedApiKey))
            {
                _logger?.Info("DistributedExtract - Worker thumbnail request rejected: invalid API key");
                return FailThumbnail("Invalid API key");
            }

            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return FailThumbnail("Path is required");
            }

            if (request.Points is null || request.Points.Count == 0)
            {
                return FailThumbnail("No extraction points");
            }

            if (_imageExtractionManager is null)
            {
                return FailThumbnail("Image extraction is unavailable");
            }

            var protocol = ParseProtocol(request.Protocol, request.Path);

            if (protocol == MediaProtocol.File && !File.Exists(request.Path))
            {
                return FailThumbnail("File not found: " + request.Path);
            }

            if (protocol != MediaProtocol.File && !IsHttpProtocol(protocol))
            {
                return FailThumbnail("Unsupported protocol: " + protocol);
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
                    ExtractChapters = false,
                    MediaType = DlnaProfileType.Video
                }, cts.Token).ConfigureAwait(false);

                if (mediaInfo?.MediaStreams is null)
                {
                    return FailThumbnail("Probe returned no result");
                }

                mediaSource.MediaStreams = mediaInfo.MediaStreams;
                mediaSource.RunTimeTicks = mediaInfo.RunTimeTicks;

                var videoStream = mediaInfo.MediaStreams
                    .FirstOrDefault(s => s.Type == MediaStreamType.Video && !s.IsExternal);

                if (videoStream is null)
                {
                    return FailThumbnail("No video stream found");
                }

                var container = Enum.TryParse<MediaContainers>(request.Container, true, out var parsed) &&
                                Enum.IsDefined(typeof(MediaContainers), parsed)
                    ? parsed
                    : (MediaContainers?)null;

                var images = new List<DistributedThumbnailImage>();

                foreach (var point in request.Points)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    var offset = TimeSpan.FromSeconds(Math.Max(0, point.OffsetSeconds));
                    var tempPath = await _imageExtractionManager
                        .ExtractVideoImage(mediaSource, request.Path, container, protocol, videoStream, null,
                            offset, cts.Token).ConfigureAwait(false);

                    if (string.IsNullOrEmpty(tempPath) || !File.Exists(tempPath))
                    {
                        return FailThumbnail("Frame extraction failed at " +
                                             offset.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s");
                    }

                    try
                    {
                        var bytes = await _fileSystem.ReadAllBytesAsync(tempPath, cts.Token).ConfigureAwait(false);

                        if (bytes.Length == 0)
                        {
                            return FailThumbnail("Frame extraction returned an empty image");
                        }

                        images.Add(new DistributedThumbnailImage
                        {
                            StartPositionTicks = point.StartPositionTicks,
                            Data = Convert.ToBase64String(bytes)
                        });
                    }
                    finally
                    {
                        try
                        {
                            File.Delete(tempPath);
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                }

                _logger?.Debug("DistributedExtract - Worker extracted " + images.Count + " frame(s) from " +
                               request.Path);

                return new DistributedThumbnailResponse
                {
                    Success = true,
                    Images = images,
                    NodeName = Plugin.Instance?.Name,
                    ElapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds
                };
            }
            catch (OperationCanceledException)
            {
                return FailThumbnail("Extraction timed out after " + timeoutSeconds + " seconds");
            }
            catch (Exception e)
            {
                _logger?.Info("DistributedExtract - Worker thumbnail extraction failed: " + request.Path +
                              " - " + e.Message);
                _logger?.Debug(e.StackTrace);
                return FailThumbnail(e.Message);
            }
        }

        /// <summary>
        /// Worker side: validates the shared key and probes the first video frame for HDR Vivid
        /// (CUVA 2094-10) metadata. Unlike the existing endpoints this one accepts local files
        /// only and requires the path to live inside one of this node's library folders.
        /// </summary>
        public async Task<DistributedHdrVividResponse> HandleWorkerHdrVividRequestAsync(
            DistributedHdrVividRequest request, CancellationToken cancellationToken)
        {
            var options = Options;

            if (!options.EnableDistributedExtract || string.IsNullOrWhiteSpace(options.SharedApiKey))
            {
                return FailHdrVivid("Distributed extraction is not enabled on this node");
            }

            if (!options.EnableDistributedHdrVivid)
            {
                return FailHdrVivid("Distributed HDR Vivid detection is not enabled on this node");
            }

            if (!SecretEquals(request?.ApiKey, options.SharedApiKey))
            {
                _logger?.Info("DistributedExtract - Worker HDR Vivid request rejected: invalid API key");
                return FailHdrVivid("Invalid API key");
            }

            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return FailHdrVivid("Path is required");
            }

            if (request.Path.IndexOf('"') >= 0)
            {
                return FailHdrVivid("Invalid path");
            }

            var protocol = ParseProtocol(request.Protocol, request.Path);

            if (protocol != MediaProtocol.File)
            {
                return FailHdrVivid("Unsupported protocol: " + protocol);
            }

            if (!IsAllowedMediaPath(request.Path))
            {
                return FailHdrVivid("Path is outside the media library: " + request.Path);
            }

            if (!File.Exists(request.Path))
            {
                return FailHdrVivid("File not found: " + request.Path);
            }

            var probePath = _ffmpegManager?.FfmpegConfiguration?.ProbePath;

            if (string.IsNullOrEmpty(probePath))
            {
                return FailHdrVivid("Ffprobe is unavailable");
            }

            var timeoutSeconds = request.TimeoutSeconds > 0
                ? Math.Max(MinRequestTimeoutSeconds, Math.Min(600, request.TimeoutSeconds))
                : Math.Max(MinRequestTimeoutSeconds, options.TimeoutSeconds);

            var started = DateTime.UtcNow;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var output = await FfmpegApi.RunAndCaptureAsync(_ffmpegManager, probePath,
                        MediaInfoApi.BuildHdrVividProbeArguments(request.Path), "HdrVividDetect",
                        (int)TimeSpan.FromSeconds(timeoutSeconds).TotalMilliseconds, cts.Token)
                    .ConfigureAwait(false);

                if (output is null)
                {
                    return FailHdrVivid("Probe did not complete");
                }

                var detected = MediaInfoApi.IsHdrVividProbeOutput(output);

                _logger?.Debug("DistributedExtract - Worker HDR Vivid detection on " + request.Path + ": " +
                               detected);

                return new DistributedHdrVividResponse
                {
                    Success = true,
                    Detected = detected,
                    NodeName = Plugin.Instance?.Name,
                    ElapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds
                };
            }
            catch (OperationCanceledException)
            {
                return FailHdrVivid("Detection timed out after " + timeoutSeconds + " seconds");
            }
            catch (Exception e)
            {
                _logger?.Info("DistributedExtract - Worker HDR Vivid detection failed: " + request.Path +
                              " - " + e.Message);
                _logger?.Debug(e.StackTrace);
                return FailHdrVivid(e.Message);
            }
        }

        /// <summary>
        /// Worker side: validates the shared key and extracts Dolby Vision Profile 5 chapter
        /// frames with the node's custom ffmpeg build. A node without Vulkan + libplacebo fails
        /// before extracting anything, so the master can fall back to its own extraction.
        /// </summary>
        public async Task<DistributedDvP5Response> HandleWorkerDvP5RequestAsync(
            DistributedDvP5Request request, CancellationToken cancellationToken)
        {
            var options = Options;

            if (!options.EnableDistributedExtract || string.IsNullOrWhiteSpace(options.SharedApiKey))
            {
                return FailDvP5("Distributed extraction is not enabled on this node");
            }

            if (!options.EnableDistributedDvP5)
            {
                return FailDvP5("Distributed Dolby Vision P5 extraction is not enabled on this node");
            }

            if (!SecretEquals(request?.ApiKey, options.SharedApiKey))
            {
                _logger?.Info("DistributedExtract - Worker DV P5 request rejected: invalid API key");
                return FailDvP5("Invalid API key");
            }

            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return FailDvP5("Path is required");
            }

            if (request.Path.IndexOf('"') >= 0)
            {
                return FailDvP5("Invalid path");
            }

            if (request.Points is null || request.Points.Count == 0)
            {
                return FailDvP5("No extraction points");
            }

            if (request.Points.Count > MaxDvP5FrameCount)
            {
                return FailDvP5("Too many extraction points");
            }

            foreach (var point in request.Points)
            {
                if (point is null || point.StartPositionTicks < 0 ||
                    double.IsNaN(point.OffsetSeconds) || double.IsInfinity(point.OffsetSeconds) ||
                    point.OffsetSeconds < 0 || point.OffsetSeconds > TimeSpan.FromDays(1).TotalSeconds)
                {
                    return FailDvP5("Invalid extraction point");
                }
            }

            if (request.Points.Select(p => p.StartPositionTicks).Distinct().Count() != request.Points.Count)
            {
                return FailDvP5("Duplicate extraction points");
            }

            var protocol = ParseProtocol(request.Protocol, request.Path);

            if (protocol != MediaProtocol.File)
            {
                return FailDvP5("Unsupported protocol: " + protocol);
            }

            if (!IsAllowedMediaPath(request.Path))
            {
                return FailDvP5("Path is outside the media library: " + request.Path);
            }

            if (!File.Exists(request.Path))
            {
                return FailDvP5("File not found: " + request.Path);
            }

            if (_ffmpegManager is null)
            {
                return FailDvP5("Ffmpeg is unavailable");
            }

            if (!DvP5Screenshot.IsAvailable(_ffmpegManager))
            {
                return FailDvP5("A custom ffmpeg with Vulkan and libplacebo is unavailable on this node");
            }

            var timeoutSeconds = request.TimeoutSeconds > 0
                ? Math.Max(MinRequestTimeoutSeconds, Math.Min(600, request.TimeoutSeconds))
                : Math.Max(MinRequestTimeoutSeconds, options.TimeoutSeconds);

            var started = DateTime.UtcNow;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var images = new List<DistributedDvP5Image>();

                foreach (var point in request.Points)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    var offset = TimeSpan.FromSeconds(point.OffsetSeconds);
                    var tempPath = await DvP5Screenshot
                        .ExtractSingleImageAsync(_ffmpegManager, request.Path, offset, cts.Token)
                        .ConfigureAwait(false);

                    if (string.IsNullOrEmpty(tempPath) || !File.Exists(tempPath))
                    {
                        return FailDvP5("Frame extraction failed at " +
                                        offset.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s");
                    }

                    try
                    {
                        var bytes = await _fileSystem.ReadAllBytesAsync(tempPath, cts.Token)
                            .ConfigureAwait(false);

                        if (bytes.Length < 4 || bytes.Length > MaxDvP5FrameBytes ||
                            bytes[0] != 0xFF || bytes[1] != 0xD8)
                        {
                            return FailDvP5("Frame extraction returned an invalid image");
                        }

                        images.Add(new DistributedDvP5Image
                        {
                            StartPositionTicks = point.StartPositionTicks,
                            Data = Convert.ToBase64String(bytes)
                        });
                    }
                    finally
                    {
                        try
                        {
                            File.Delete(tempPath);
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                }

                _logger?.Debug("DistributedExtract - Worker extracted " + images.Count +
                               " Dolby Vision P5 frame(s) from " + request.Path);

                return new DistributedDvP5Response
                {
                    Success = true,
                    Images = images,
                    NodeName = Plugin.Instance?.Name,
                    ElapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds
                };
            }
            catch (OperationCanceledException)
            {
                return FailDvP5("Extraction timed out after " + timeoutSeconds + " seconds");
            }
            catch (Exception e)
            {
                _logger?.Info("DistributedExtract - Worker DV P5 extraction failed: " + request.Path +
                              " - " + e.Message);
                _logger?.Debug(e.StackTrace);
                return FailDvP5(e.Message);
            }
        }

        /// <summary>
        /// Worker side: validates the shared key and creates a raw chromaprint fingerprint with
        /// the same ffmpeg arguments Emby uses for intro detection.
        /// </summary>
        public async Task<DistributedFingerprintResponse> HandleWorkerFingerprintRequestAsync(
            DistributedFingerprintRequest request, CancellationToken cancellationToken)
        {
            var options = Options;

            if (!options.EnableDistributedExtract || string.IsNullOrWhiteSpace(options.SharedApiKey))
            {
                return FailFingerprint("Distributed extraction is not enabled on this node");
            }

            if (!options.EnableDistributedFingerprint)
            {
                return FailFingerprint("Distributed fingerprint extraction is not enabled on this node");
            }

            if (!SecretEquals(request?.ApiKey, options.SharedApiKey))
            {
                _logger?.Info("DistributedExtract - Worker fingerprint request rejected: invalid API key");
                return FailFingerprint("Invalid API key");
            }

            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return FailFingerprint("Path is required");
            }

            if (request.Minutes < 1 || request.Minutes > 30)
            {
                return FailFingerprint("Invalid fingerprint duration: " + request.Minutes);
            }

            if (_ffmpegManager is null || _mediaEncoder is null)
            {
                return FailFingerprint("Ffmpeg is unavailable");
            }

            if (_ffmpegManager.FfmpegCapabilities?.SupportsChromaprint != true)
            {
                return FailFingerprint("Chromaprint is not supported by ffmpeg on this node");
            }

            var protocol = ParseProtocol(request.Protocol, request.Path);

            if (protocol == MediaProtocol.File && !File.Exists(request.Path))
            {
                return FailFingerprint("File not found: " + request.Path);
            }

            if (protocol != MediaProtocol.File && !IsHttpProtocol(protocol))
            {
                return FailFingerprint("Unsupported protocol: " + protocol);
            }

            var timeoutSeconds = request.TimeoutSeconds > 0
                ? Math.Max(MinRequestTimeoutSeconds, Math.Min(600, request.TimeoutSeconds))
                : Math.Max(MinRequestTimeoutSeconds, options.TimeoutSeconds);

            var started = DateTime.UtcNow;
            var tempPath = Path.Combine(_applicationPaths.TempDirectory, Guid.NewGuid().ToString("N"));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var duration = TimeSpan.FromMinutes(request.Minutes);
                var inputArgument = _mediaEncoder.GetInputArgument(request.Path.AsSpan(), protocol);

                var arguments = string.Join(" ", new[]
                {
                    "-i " + inputArgument,
                    "-t " + _mediaEncoder.GetTimeParameter(duration.Ticks),
                    "-ac 1",
                    "-acodec pcm_s16le",
                    "-ar " + FingerprintSampleRate.ToString(CultureInfo.InvariantCulture),
                    "-vn",
                    "-sn",
                    "-f chromaprint",
                    "-fp_format raw",
                    "\"" + tempPath + "\""
                });

                using var runner = _ffmpegManager.CreateFfMpegRunner("CreateTitleFingerprint", null);
                runner.Start(arguments);

                if (!await runner.WaitForExitAsync((int)TimeSpan.FromSeconds(timeoutSeconds).TotalMilliseconds,
                        cts.Token).ConfigureAwait(false))
                {
                    throw new TimeoutException("Operation timed out after " + timeoutSeconds + " seconds");
                }

                if (runner.LastExitCode.GetValueOrDefault() == -1 || !File.Exists(tempPath))
                {
                    runner.LogOutput(LogSeverity.Error);
                    return FailFingerprint("Fingerprint extraction failed");
                }

                var bytes = await _fileSystem.ReadAllBytesAsync(tempPath, cts.Token).ConfigureAwait(false);

                if (bytes.Length == 0 || bytes.Length % 4 != 0)
                {
                    return FailFingerprint("Fingerprint output is empty or invalid");
                }

                _logger?.Debug("DistributedExtract - Worker created fingerprint (" + bytes.Length +
                               " bytes) for " + request.Path);

                return new DistributedFingerprintResponse
                {
                    Success = true,
                    Data = Convert.ToBase64String(bytes),
                    NodeName = Plugin.Instance?.Name,
                    ElapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds
                };
            }
            catch (OperationCanceledException)
            {
                return FailFingerprint("Fingerprint extraction timed out after " + timeoutSeconds + " seconds");
            }
            catch (Exception e)
            {
                _logger?.Info("DistributedExtract - Worker fingerprint extraction failed: " + request.Path +
                              " - " + e.Message);
                _logger?.Debug(e.StackTrace);
                return FailFingerprint(e.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private async Task<TResponse> SendAsync<TRequest, TResponse>(string node, string endpoint,
            TRequest request, TimeSpan budget, CancellationToken cancellationToken) where TResponse : class
        {
            var timeoutMs = Math.Max(1000, (int)Math.Min(int.MaxValue, budget.TotalMilliseconds));

            var httpOptions = new HttpRequestOptions
            {
                Url = node + endpoint,
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

            return _jsonSerializer.DeserializeFromString<TResponse>(body);
        }

        private bool TryApply(BaseItem item, DistributedMediaInfoResponse response, string mediaPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(response?.FfprobeJson)) return false;

            var mediaInfo = CustomFfprobeApi.NormalizeProbeJson(response.FfprobeJson, item is Audio, mediaPath,
                MediaProtocol.File);

            if (mediaInfo is null) return false;

            var streams = mediaInfo.MediaStreams?
                .Where(s => (s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio ||
                             s.Type == MediaStreamType.Subtitle) &&
                            !(s.IsExternal && s.Type == MediaStreamType.Subtitle))
                .ToList() ?? new List<MediaStream>();

            if (streams.Count == 0 ||
                streams.All(s => s.Type != MediaStreamType.Video && s.Type != MediaStreamType.Audio))
            {
                return false;
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

            return ApplyMediaInfoPayload(item, payload, cancellationToken);
        }

        private bool ApplyMediaInfoPayload(BaseItem item, MediaInfoPayload payload,
            CancellationToken cancellationToken)
        {
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

        private bool TryApplyChapterImages(Video item, List<DistributedThumbnailPoint> points,
            List<ChapterInfo> chapters, DistributedThumbnailResponse response)
        {
            var images = response?.Images;

            if (images is null || images.Count != points.Count) return false;

            var decoded = new List<KeyValuePair<long, byte[]>>();

            foreach (var point in points)
            {
                var image = images.FirstOrDefault(i =>
                    i.StartPositionTicks == point.StartPositionTicks && !string.IsNullOrEmpty(i.Data));

                if (image is null) return false;

                byte[] bytes;

                try
                {
                    bytes = Convert.FromBase64String(image.Data);
                }
                catch (FormatException)
                {
                    return false;
                }

                if (bytes.Length < 4) return false;

                decoded.Add(new KeyValuePair<long, byte[]>(point.StartPositionTicks, bytes));
            }

            var chaptersPath = Path.Combine(item.GetInternalMetadataPath(), "chapters");
            _fileSystem.CreateDirectory(chaptersPath);

            // Chapter images that were already on disk keep their records in sync too.
            foreach (var chapter in chapters)
            {
                if (chapter.StartPositionTicks >= item.RunTimeTicks.GetValueOrDefault()) break;

                var existingPath = GetChapterImagePath(item, chapter.StartPositionTicks);

                if (File.Exists(existingPath))
                {
                    chapter.ImagePath = existingPath;
                    chapter.ImageDateModified = _fileSystem.GetLastWriteTimeUtc(existingPath);
                }
            }

            foreach (var pair in decoded)
            {
                var imagePath = GetChapterImagePath(item, pair.Key);
                var tempPath = imagePath + ".tmp";

                try
                {
                    _fileSystem.WriteAllBytes(tempPath, pair.Value);
                    _fileSystem.MoveFile(tempPath, imagePath, true);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                var chapter = chapters.FirstOrDefault(c => c.StartPositionTicks == pair.Key);

                if (chapter != null)
                {
                    chapter.ImagePath = imagePath;
                    chapter.ImageDateModified = _fileSystem.GetLastWriteTimeUtc(imagePath);
                }
            }

            DeleteDeadChapterImages(chaptersPath, chapters);

            // Same overload Emby uses: saving with clearExtractionFailureResult also clears a
            // previous thumbnail extraction failure record for this item.
            _itemRepository.SaveChapters(item.InternalId, true, chapters);

            return true;
        }

        private void DeleteDeadChapterImages(string chaptersPath, List<ChapterInfo> chapters)
        {
            if (!Directory.Exists(chaptersPath)) return;

            var keep = new HashSet<string>(
                chapters.Where(c => !string.IsNullOrEmpty(c.ImagePath)).Select(c => c.ImagePath),
                StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var file in Directory.GetFiles(chaptersPath))
                {
                    if (keep.Contains(file)) continue;

                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            catch (Exception e)
            {
                _logger?.Debug("DistributedExtract - Failed to clean chapter images: " + e.Message);
            }
        }

        private bool TryApplyTitleFingerprint(string targetPath, DistributedFingerprintResponse response)
        {
            byte[] data;

            try
            {
                data = Convert.FromBase64String(response.Data);
            }
            catch (FormatException)
            {
                return false;
            }

            if (data.Length == 0 || data.Length % 4 != 0) return false;

            var directory = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrEmpty(directory)) _fileSystem.CreateDirectory(directory);

            var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                _fileSystem.WriteAllBytes(tempPath, data);
                _fileSystem.MoveFile(tempPath, targetPath, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // ignored
                }
            }

            return File.Exists(targetPath);
        }

        private static string GetChapterImagePath(Video item, long chapterPositionTicks)
        {
            // Mirrors Emby's ThumbnailGenerator.GetChapterImagePath().
            var fileName = item.DateModified.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "_" +
                           chapterPositionTicks.ToString(CultureInfo.InvariantCulture) + ".jpg";

            return Path.Combine(item.GetInternalMetadataPath(), "chapters", fileName);
        }

        private static string GetTitleFingerprintPath(Episode item, int minutes)
        {
            // Mirrors Emby's AudioFingerprintManager.GetTitleFingerprintFileName().
            var fileName = "title_" + minutes.ToString(CultureInfo.InvariantCulture) + "_" +
                           item.DateCreated.Ticks.ToString(CultureInfo.InvariantCulture) + ".fp";

            return Path.Combine(item.GetInternalMetadataPath(), fileName);
        }

        private static int GetTitleFingerprintDurationMinutes(LibraryOptions libraryOptions)
        {
            // Mirrors Emby's AudioFingerprintManager.GetTitleFingerprintDurationMinutes().
            var length = libraryOptions?.IntroDetectionFingerprintLength ?? 10;

            return length >= 2 && length < 30 ? length : 10;
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

        private static bool IsLocalDistributablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            if (path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)) return false;

            if (GetProtocol(path) != MediaProtocol.File) return false;

            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            if (extension == "iso" || extension == "img" || extension == "strm") return false;

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

        /// <summary>
        /// New endpoints accept local files only. The path is normalized and must live inside
        /// one of this node's library folders. Symbolic links are not resolved because
        /// netstandard2.1 has no ResolveLinkTarget; the library roots and the worker user's
        /// permissions remain the trust boundary.
        /// </summary>
        private bool IsAllowedMediaPath(string path)
        {
            return TryNormalizeAllowedMediaPath(path, out _);
        }

        private bool TryNormalizeAllowedMediaPath(string path, out string fullPath)
        {
            fullPath = null;

            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;

            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return false;
            }

            foreach (var root in GetMediaRoots())
            {
                if (IsPathUnderRoot(fullPath, root)) return true;
            }

            fullPath = null;
            return false;
        }

        private static bool IsRejectedMediaPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("bluray:", StringComparison.OrdinalIgnoreCase) ||
                path.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            return extension == "strm" || extension == "iso" || extension == "img";
        }

        private static bool IsDiscFolderPath(string fullPath)
        {
            var segments = fullPath.Split(new[]
            {
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar
            }, StringSplitOptions.RemoveEmptyEntries);

            return segments.Any(s =>
                string.Equals(s, "BDMV", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "VIDEO_TS", StringComparison.OrdinalIgnoreCase));
        }

        private MediaInfoDistributionAttempt MediaInfoFallback(BaseItem item, MediaInfoFallbackReason reason)
        {
            LogMediaInfoFallback(item, reason);

            return new MediaInfoDistributionAttempt
            {
                Attempted = true,
                FallbackReason = reason
            };
        }

        private void LogMediaInfoFallback(BaseItem item, MediaInfoFallbackReason reason)
        {
            _logger?.Info("DistributedExtract - Media info fallback (" + GetFallbackReasonCode(reason) + "): " +
                          item?.Name + " - " + item?.Path);
        }

        private static string GetFallbackReasonCode(MediaInfoFallbackReason reason)
        {
            switch (reason)
            {
                case MediaInfoFallbackReason.NoWorker:
                    return "no_worker";
                case MediaInfoFallbackReason.Timeout:
                    return "timeout";
                case MediaInfoFallbackReason.WorkerError:
                    return "worker_error";
                case MediaInfoFallbackReason.PathNotAllowed:
                    return "path_not_allowed";
                default:
                    return "worker_error";
            }
        }

        private List<string> GetMediaRoots()
        {
            var now = DateTime.UtcNow;

            lock (_mediaRootsLock)
            {
                if (_mediaRootsCache != null && now - _mediaRootsCacheUtc < MediaRootsCacheDuration)
                {
                    return _mediaRootsCache;
                }

                List<string> roots;

                try
                {
                    roots = _libraryManager.GetVirtualFolders()
                        .Where(f => f?.Locations != null)
                        .SelectMany(f => f.Locations)
                        .Select(NormalizeRoot)
                        .Where(r => !string.IsNullOrEmpty(r))
                        .Distinct(Path.DirectorySeparatorChar == '\\'
                            ? StringComparer.OrdinalIgnoreCase
                            : StringComparer.Ordinal)
                        .ToList();
                }
                catch (Exception e)
                {
                    _logger?.Debug("DistributedExtract - Failed to read media library roots: " + e.Message);
                    roots = new List<string>();
                }

                _mediaRootsCache = roots;
                _mediaRootsCacheUtc = now;

                return roots;
            }
        }

        private static string NormalizeRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                var full = Path.GetFullPath(path);

                if (full.Length > 1)
                {
                    full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                return full;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsPathUnderRoot(string fullPath, string root)
        {
            if (string.Equals(fullPath, root, PathComparison)) return true;

            var separator = root.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? string.Empty
                : Path.DirectorySeparatorChar.ToString();

            return fullPath.StartsWith(root + separator, PathComparison);
        }

        private static bool SecretEquals(string provided, string expected)
        {
            if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;

            var providedBytes = Encoding.UTF8.GetBytes(provided);
            var expectedBytes = Encoding.UTF8.GetBytes(expected);

            return providedBytes.Length == expectedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
        }

        private static DistributedMediaInfoResponse Fail(string error, string errorCode = "worker_error")
        {
            return new DistributedMediaInfoResponse { Success = false, Error = error, ErrorCode = errorCode };
        }

        private static DistributedThumbnailResponse FailThumbnail(string error)
        {
            return new DistributedThumbnailResponse { Success = false, Error = error };
        }

        private static DistributedFingerprintResponse FailFingerprint(string error)
        {
            return new DistributedFingerprintResponse { Success = false, Error = error };
        }

        private static DistributedHdrVividResponse FailHdrVivid(string error)
        {
            return new DistributedHdrVividResponse { Success = false, Error = error };
        }

        private static DistributedDvP5Response FailDvP5(string error)
        {
            return new DistributedDvP5Response { Success = false, Error = error };
        }
    }
}
