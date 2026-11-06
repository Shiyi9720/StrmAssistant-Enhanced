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

        public static bool IsFingerprintDistributionEnabled => IsFingerprintEnabled;

        private static bool IsThumbnailEnabled => IsEnabled && Options.EnableDistributedThumbnail;

        private static bool IsFingerprintEnabled => IsEnabled && Options.EnableDistributedFingerprint;

        /// <summary>
        /// Tries the configured worker nodes in turn and applies the first usable result.
        /// Returns false immediately when the feature is disabled and on every failure.
        /// </summary>
        public async Task<bool> TryExtractAsync(BaseItem item, string mediaPath, string source,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || !(item is Video || item is Audio)) return false;

            if (string.IsNullOrWhiteSpace(mediaPath) || !IsDistributable(item, mediaPath)) return false;

            return await TryWorkersAsync<DistributedMediaInfoRequest, DistributedMediaInfoResponse>(
                source, item.Name + " - " + item.Path, "/StrmAssistant/DistributedExtract/MediaInfo",
                remaining =>
                {
                    var options = Options;
                    var mappedPath = MapPath(mediaPath, options.PathMapping);

                    return new DistributedMediaInfoRequest
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
                },
                response => response?.Success == true,
                response => response?.Error,
                response => TryApply(item, response, cancellationToken),
                cancellationToken).ConfigureAwait(false);
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

        private async Task<bool> TryWorkersAsync<TRequest, TResponse>(string source, string itemDescription,
            string endpoint, Func<TimeSpan, TRequest> requestFactory, Func<TResponse, bool> isSuccess,
            Func<TResponse, string> getError, Func<TResponse, bool> apply, CancellationToken cancellationToken)
            where TResponse : class
        {
            try
            {
                if (!IsEnabled) return false;

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
                    var request = requestFactory(remaining);

                    TResponse response;

                    try
                    {
                        response = await SendAsync<TRequest, TResponse>(node, endpoint, request, remaining,
                            cancellationToken).ConfigureAwait(false);
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

                    if (response is null || !isSuccess(response))
                    {
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
                        return false;
                    }

                    if (!applied)
                    {
                        _logger?.Debug("DistributedExtract - Unusable payload from " + node + ": " + itemDescription);
                        continue;
                    }

                    Interlocked.Exchange(ref health.Failures, 0);
                    Interlocked.Exchange(ref health.BlockedUntilTicks, 0);

                    _logger?.Info("DistributedExtract - Extracted via " + node + " (" + source +
                                  "): " + itemDescription);

                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger?.Debug("DistributedExtract - Failed: " + itemDescription + " - " + e.Message);
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

        private static DistributedThumbnailResponse FailThumbnail(string error)
        {
            return new DistributedThumbnailResponse { Success = false, Error = error };
        }

        private static DistributedFingerprintResponse FailFingerprint(string error)
        {
            return new DistributedFingerprintResponse { Success = false, Error = error };
        }
    }
}
