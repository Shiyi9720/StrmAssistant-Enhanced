using Emby.Media.Model.ProbeModel;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Locates a static ffprobe build that can read disc images over a remote protocol.
    /// Emby's bundled ffprobe has no bluray protocol and therefore cannot probe an ISO, and it
    /// cannot follow an http/smb shortcut either. The static builds published for this plugin
    /// (GPL, with libbluray and the bluray/http bridge) are resolved in this order:
    ///  1. the path configured in the plugin settings,
    ///  2. the plugin data folder (bin/ffprobe),
    ///  3. Emby's config/bin folder (the location used by the upstream plugin),
    ///  4. a first-use download into the plugin data folder for the current platform.
    /// No candidate means the caller silently falls back to the regular probe path.
    /// </summary>
    internal static class CustomFfprobeApi
    {
        private const string StaticFfprobeVersion = "9.0.2";

        private const string StaticFfprobeUrl =
            "https://raw.githubusercontent.com/sjtuross/StrmAssistant.Releases/main/static-ffprobe/{0}/ffprobe_" +
            StaticFfprobeVersion + ".zip";

        private static readonly object DownloadLock = new object();
        private static readonly SemaphoreSlim DownloadGate = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentDictionary<string, byte> Validated =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> Invalid =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private static Task<string> _downloadTask;
        private static DateTime _nextDownloadAttemptUtc = DateTime.MinValue;

        private static ILogger Logger => Plugin.Instance?.Logger;

        private static string ExecutableName =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";

        internal static bool IsHttpUrl(string path)
        {
            return path != null &&
                   (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsSmbUrl(string path)
        {
            return path != null && path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsRemoteUrl(string path)
        {
            return IsHttpUrl(path) || IsSmbUrl(path);
        }

        internal static string NormalizeInput(string inputPath)
        {
            if (string.IsNullOrEmpty(inputPath)) return inputPath;

            const string blurayPrefix = "bluray:";

            return inputPath.StartsWith(blurayPrefix, StringComparison.OrdinalIgnoreCase)
                ? blurayPrefix + StrmUrlHelper.Normalize(inputPath.Substring(blurayPrefix.Length))
                : StrmUrlHelper.Normalize(inputPath);
        }

        /// <summary>
        /// Returns a validated ffprobe path, or null when no static build is available. The
        /// first call may start a background download; a short wait is used so a first-time
        /// remote extraction can still use it, while later scans pick it up without blocking.
        /// </summary>
        internal static async Task<string> GetPathAsync(CancellationToken cancellationToken)
        {
            try
            {
                var configured = Plugin.Instance?.GetPluginOptions().MediaInfoExtractOptions
                    .CustomFfprobePath?.Trim().Trim('"');

                if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                {
                    return await ValidateAsync(configured, cancellationToken).ConfigureAwait(false)
                        ? configured
                        : null;
                }

                foreach (var candidate in GetStandardPaths())
                {
                    if (!File.Exists(candidate)) continue;

                    if (await ValidateAsync(candidate, cancellationToken).ConfigureAwait(false))
                    {
                        return candidate;
                    }
                }

                var download = EnsureDownloadedAsync();

                if (download != null)
                {
                    var completed = await Task.WhenAny(download,
                            Task.Delay(TimeSpan.FromSeconds(20), cancellationToken))
                        .ConfigureAwait(false);

                    if (completed == download)
                    {
                        var path = await download.ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(path) &&
                            await ValidateAsync(path, cancellationToken).ConfigureAwait(false))
                        {
                            return path;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception e)
            {
                Logger?.Debug("CustomFfprobe - Resolve failed: " + e.Message);
            }

            return null;
        }

        private static string[] GetStandardPaths()
        {
            var dataFolder = Plugin.Instance?.DataFolderPath;

            if (string.IsNullOrEmpty(dataFolder))
            {
                dataFolder = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath,
                    "StrmAssistant");
            }

            var programData = Plugin.Instance?.ApplicationPaths?.ProgramDataPath;

            return new[]
            {
                Path.Combine(dataFolder, "bin", ExecutableName),
                string.IsNullOrEmpty(programData)
                    ? null
                    : Path.Combine(programData, "config", "bin", ExecutableName)
            }.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        }

        private static async Task<bool> ValidateAsync(string path, CancellationToken cancellationToken)
        {
            string key;

            try
            {
                var info = new FileInfo(path);

                if (!info.Exists || info.Length == 0) return false;

                key = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                return false;
            }

            if (Validated.ContainsKey(key)) return true;
            if (Invalid.ContainsKey(key)) return false;

            try
            {
                var output = await FfmpegApi.RunAndCaptureAsync(Plugin.Instance?.FfmpegManager, path, "-version",
                    "CustomFfprobeVersion", 15000, cancellationToken).ConfigureAwait(false);

                if (output != null &&
                    output.IndexOf("ffprobe version", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Validated[key] = 0;
                    Logger?.Info("CustomFfprobe - Using " + path);

                    return true;
                }
            }
            catch (Exception e)
            {
                Logger?.Debug("CustomFfprobe - Validation failed: " + e.Message);
            }

            Invalid[key] = 0;
            Logger?.Info("CustomFfprobe - Not a usable ffprobe binary: " + path);

            return false;
        }

        private static Task<string> EnsureDownloadedAsync()
        {
            lock (DownloadLock)
            {
                if (_downloadTask != null) return _downloadTask;

                // Do not retry a failed download for every item; a missing network or an
                // unsupported platform would otherwise stall each scan.
                if (DateTime.UtcNow < _nextDownloadAttemptUtc) return null;

                return _downloadTask = DownloadAsync();
            }
        }

        private static async Task<string> DownloadAsync()
        {
            await DownloadGate.WaitAsync().ConfigureAwait(false);

            var success = false;

            try
            {
                var platform = GetPlatformFolder();

                if (platform == null)
                {
                    Logger?.Info("CustomFfprobe - No static ffprobe build for this platform: " +
                                 RuntimeInformation.OSDescription + " / " +
                                 RuntimeInformation.ProcessArchitecture);

                    return null;
                }

                var destination = GetStandardPaths().First();
                var folder = Path.GetDirectoryName(destination);

                if (string.IsNullOrEmpty(folder)) return null;

                Directory.CreateDirectory(folder);

                if (File.Exists(destination) &&
                    await ValidateAsync(destination, CancellationToken.None).ConfigureAwait(false))
                {
                    success = true;
                    return destination;
                }

                var tempZip = Path.Combine(Path.GetTempPath(),
                    "strmassistant-ffprobe-" + Guid.NewGuid().ToString("N") + ".zip");

                try
                {
                    Logger?.Info("CustomFfprobe - Downloading static ffprobe for " + platform);

                    var httpClient = Plugin.Instance?.HttpClient;

                    if (httpClient is null) return null;

                    using (var response = await httpClient.SendAsync(new HttpRequestOptions
                           {
                               Url = string.Format(StaticFfprobeUrl, platform),
                               DownloadFilePath = tempZip,
                               BufferContent = false,
                               TimeoutMs = 600000,
                               ThrowOnErrorResponse = false,
                               LogRequest = false,
                               LogResponse = false,
                               LogErrors = false,
                               UserAgent = Plugin.Instance?.UserAgent
                           }, "GET").ConfigureAwait(false))
                    {
                        if (response.StatusCode != HttpStatusCode.OK &&
                            response.StatusCode != HttpStatusCode.PartialContent)
                        {
                            Logger?.Info("CustomFfprobe - Download failed: HTTP " +
                                         (int)response.StatusCode);

                            return null;
                        }
                    }

                    if (!File.Exists(tempZip)) return null;

                    using (var archive = ZipFile.OpenRead(tempZip))
                    {
                        var entry = archive.Entries.FirstOrDefault(e =>
                            string.Equals(e.Name, "ffprobe", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(e.Name, "ffprobe.exe", StringComparison.OrdinalIgnoreCase));

                        if (entry is null) return null;

                        entry.ExtractToFile(destination, true);
                    }

                    MakeExecutable(destination);

                    if (await ValidateAsync(destination, CancellationToken.None).ConfigureAwait(false))
                    {
                        Logger?.Info("CustomFfprobe - Downloaded to " + destination);
                        success = true;

                        return destination;
                    }

                    return null;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempZip)) File.Delete(tempZip);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            catch (Exception e)
            {
                Logger?.Info("CustomFfprobe - Download failed: " + e.Message);

                return null;
            }
            finally
            {
                DownloadGate.Release();

                if (!success)
                {
                    _nextDownloadAttemptUtc = DateTime.UtcNow.AddHours(1);
                }

                lock (DownloadLock)
                {
                    _downloadTask = null;
                }
            }
        }

        private static string GetPlatformFolder()
        {
            var architecture = RuntimeInformation.ProcessArchitecture;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (architecture == Architecture.X64) return "linux_x64";
                if (architecture == Architecture.Arm64) return "linux_arm64";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (architecture == Architecture.X64) return "osx_x64";
                if (architecture == Architecture.Arm64) return "osx_arm64";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                     architecture == Architecture.X64)
            {
                return "win_x64";
            }

            return null;
        }

        private static void MakeExecutable(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = "+x \"" + path + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                process?.WaitForExit(5000);
            }
            catch (Exception e)
            {
                Logger?.Debug("CustomFfprobe - chmod failed: " + e.Message);
            }
        }

        /// <summary>
        /// Probes the given input (for example "bluray:http://host/movie.iso") with the static
        /// build and normalizes the result the same way Emby's probe manager does. Returns null
        /// on any failure so callers can fall back to their existing behaviour.
        /// </summary>
        internal static async Task<MediaInfo> ProbeAsync(string inputPath, bool isAudio,
            MediaProtocol protocol, CancellationToken cancellationToken)
        {
            inputPath = NormalizeInput(inputPath);

            var json = await RunProbeJsonAsync(inputPath,
                "-show_streams -show_chapters -show_format",
                cancellationToken).ConfigureAwait(false);

            return NormalizeProbeJson(json, isAudio, inputPath, protocol);
        }

        /// <summary>
        /// Deserializes raw ffprobe JSON and runs it through the same normalizer as an ordinary
        /// Emby probe. The distributed media-info endpoint returns the raw JSON so that the
        /// master, rather than the worker, owns the conversion into Emby's media model.
        /// </summary>
        internal static MediaInfo NormalizeProbeJson(string json, bool isAudio, string inputPath,
            MediaProtocol protocol)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            inputPath = NormalizeInput(inputPath);

            try
            {
                var probeResult = Plugin.Instance?.JsonSerializer
                    .DeserializeFromString<ProbeResult>(json);

                if (probeResult is null ||
                    (probeResult.streams is null && probeResult.format is null))
                {
                    return null;
                }

                return Normalize(probeResult, isAudio, inputPath, protocol);
            }
            catch (Exception e)
            {
                Logger?.Debug("CustomFfprobe - Probe failed: " + e.Message);

                return null;
            }
        }

        /// <summary>
        /// Runs the static ffprobe with the given output options and returns the raw JSON object.
        /// Some signals are not part of Emby's ProbeResult, notably `-show_stream_groups`, which
        /// is the only way a UHD Blu-ray Dolby Vision Profile 7 enhancement layer is visible.
        /// Returns null when the static build is unavailable or the probe fails, so callers can
        /// keep their existing behaviour.
        /// </summary>
        internal static Task<string> RunProbeJsonAsync(string inputPath, string outputArguments,
            CancellationToken cancellationToken)
        {
            return RunProbeJsonAsync(inputPath, outputArguments, 120000, cancellationToken);
        }

        internal static async Task<string> RunProbeJsonAsync(string inputPath, string outputArguments,
            int timeoutMs, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(inputPath)) return null;

            inputPath = NormalizeInput(inputPath);

            var exePath = await GetPathAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(exePath)) return null;

            try
            {
                var arguments = "-v error -print_format json " + (outputArguments ?? string.Empty) +
                                " \"" + inputPath + "\"";

                var output = await FfmpegApi.RunAndCaptureAsync(Plugin.Instance?.FfmpegManager, exePath, arguments,
                    "CustomFfprobe", timeoutMs > 0 ? timeoutMs : 120000, cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(output)) return null;

                var start = output.IndexOf('{');
                var end = output.LastIndexOf('}');

                return start < 0 || end <= start ? null : output.Substring(start, end - start + 1);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                Logger?.Debug("CustomFfprobe - Probe failed: " + e.Message);

                return null;
            }
        }

        /// <summary>
        /// Emby's ProbeResultNormalizer lives in Emby.Server.MediaEncoding, which is not one of
        /// the reference assemblies this plugin compiles against. Resolve it at runtime (the
        /// assembly is always loaded together with the probe manager) so a probe result goes
        /// through exactly the same normalization as an ordinary Emby probe.
        /// </summary>
        private static MediaInfo Normalize(ProbeResult probeResult, bool isAudio, string path,
            MediaProtocol protocol)
        {
            var type = Type.GetType(
                "Emby.Server.MediaEncoding.Probing.ProbeResultNormalizer, Emby.Server.MediaEncoding");

            if (type is null) return null;

            var constructor = type.GetConstructor(new[]
            {
                typeof(ILogger), typeof(IFileSystem), typeof(ILocalizationManager)
            });

            if (constructor is null) return null;

            var normalizer = constructor.Invoke(new object[]
            {
                Plugin.Instance?.Logger, Plugin.Instance?.FileSystem, Plugin.Instance?.LocalizationManager
            });

            var method = type.GetMethod("GetMediaInfo", new[]
            {
                typeof(ProbeResult), typeof(bool), typeof(string), typeof(MediaProtocol)
            });

            return method?.Invoke(normalizer, new object[] { probeResult, isAudio, path, protocol })
                as MediaInfo;
        }
    }
}
