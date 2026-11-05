using Emby.Media.Model.DiagnosticsModel;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Wraps Emby's ffmpeg manager so that user supplied arguments are added to the
    /// ffprobe/ffmpeg commands issued by the plugin's own extraction components
    /// (intro fingerprint audio extraction and chapter image extraction).
    /// </summary>
    public static class FfmpegApi
    {
        private static readonly Regex InputFlagRegex = new Regex(@"(?<!\S)-i(?=\s|$)", RegexOptions.Compiled);

        public static IFfmpegManager WrapFfmpegManager(IFfmpegManager ffmpegManager)
        {
            if (ffmpegManager is null || ffmpegManager is FfmpegManagerWithArguments) return ffmpegManager;

            return new FfmpegManagerWithArguments(ffmpegManager);
        }

        internal static string ApplyProbeArguments(string commandLineArgs)
        {
            var extraArguments = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions
                .CustomFfprobeArguments;

            return InjectArguments(commandLineArgs, extraArguments, true);
        }

        internal static string ApplyEncoderArguments(string commandLineArgs)
        {
            var extraArguments = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions
                .CustomFfmpegArguments;

            return InjectArguments(commandLineArgs, extraArguments, false);
        }

        internal static string InjectArguments(string commandLineArgs, string extraArguments, bool isProbe)
        {
            if (string.IsNullOrWhiteSpace(commandLineArgs) || string.IsNullOrWhiteSpace(extraArguments))
            {
                return commandLineArgs;
            }

            var extraTokens = Tokenize(extraArguments);

            if (extraTokens.Count == 0) return commandLineArgs;

            var existingFlags = new HashSet<string>(Tokenize(commandLineArgs).Where(IsFlag),
                StringComparer.OrdinalIgnoreCase);
            var injectedFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var injectedTokens = new List<string>();
            var skipValue = false;

            foreach (var token in extraTokens)
            {
                if (IsFlag(token))
                {
                    skipValue = false;

                    if (existingFlags.Contains(token) || !injectedFlags.Add(token))
                    {
                        // The flag is already provided by Emby (or by the user twice).
                        // Skip it together with its value so no orphan value is appended.
                        skipValue = true;
                        continue;
                    }

                    injectedTokens.Add(token);
                }
                else if (skipValue)
                {
                    skipValue = false;
                }
                else
                {
                    injectedTokens.Add(token);
                }
            }

            if (injectedTokens.Count == 0) return commandLineArgs;

            var injection = string.Join(" ", injectedTokens.Select(QuoteToken));

            if (isProbe)
            {
                // ffprobe input options must precede the input file.
                var match = InputFlagRegex.Match(commandLineArgs);

                if (match.Success)
                {
                    var before = commandLineArgs.Substring(0, match.Index).TrimEnd();
                    var after = commandLineArgs.Substring(match.Index);

                    return (before.Length > 0 ? before + " " : string.Empty) + injection + " " + after;
                }
            }

            return commandLineArgs.TrimEnd() + " " + injection;
        }

        /// <summary>
        /// User selected ffmpeg build. Used for Dolby Vision Profile 5 chapter images, which need
        /// Vulkan + libplacebo to apply the DOVI reshaping before tone mapping.
        /// </summary>
        internal static string GetCustomFfmpegPath()
        {
            var path = Plugin.Instance?.GetPluginOptions().MediaInfoExtractOptions.CustomFfmpegPath;

            if (string.IsNullOrWhiteSpace(path)) return null;

            path = path.Trim().Trim('"');

            return File.Exists(path) ? path : null;
        }

        internal static string GetEncoderPath(IFfmpegManager ffmpegManager)
        {
            return GetCustomFfmpegPath() ?? ffmpegManager?.FfmpegConfiguration?.EncoderPath;
        }

        internal static string GetProbePath(IFfmpegManager ffmpegManager)
        {
            return ffmpegManager?.FfmpegConfiguration?.ProbePath;
        }

        /// <summary>
        /// Emby's ffmpeg runner can write the process output to a log file. That is the only way
        /// to read ffprobe/filter output through the public runner API.
        /// </summary>
        internal static async Task<string> RunAndCaptureAsync(IFfmpegManager ffmpegManager, string exePath,
            string arguments, string name, int timeoutMs, CancellationToken cancellationToken)
        {
            if (ffmpegManager is null || string.IsNullOrEmpty(exePath)) return null;

            var logPath = Path.Combine(Path.GetTempPath(),
                "strmassistant-" + Guid.NewGuid().ToString("N") + ".log");

            try
            {
                using (var runner = ffmpegManager.CreateFfMpegRunner(name, logPath))
                {
                    if (runner is null) return null;

                    if (!runner.Run(exePath, arguments))
                    {
                        return null;
                    }

                    var exited = await runner.WaitForExitAsync(timeoutMs, cancellationToken)
                        .ConfigureAwait(false);

                    if (!exited)
                    {
                        await runner.WaitAndKillAsync(2000).ConfigureAwait(false);
                        return null;
                    }

                    // Output is flushed when the process exits; give the writer a moment.
                    for (var i = 0; i < 10; i++)
                    {
                        if (File.Exists(logPath) && new FileInfo(logPath).Length > 0) break;
                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    }

                    return File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                try
                {
                    if (File.Exists(logPath)) File.Delete(logPath);
                }
                catch
                {
                    // ignored
                }
            }
        }

        internal static async Task<bool> RunAsync(IFfmpegManager ffmpegManager, string exePath, string arguments,
            string name, int timeoutMs, CancellationToken cancellationToken)
        {
            if (ffmpegManager is null || string.IsNullOrEmpty(exePath)) return false;

            var logPath = Path.Combine(Path.GetTempPath(),
                "strmassistant-" + Guid.NewGuid().ToString("N") + ".log");

            try
            {
                using (var runner = ffmpegManager.CreateFfMpegRunner(name, logPath))
                {
                    if (runner is null || !runner.Run(exePath, arguments)) return false;

                    var exited = await runner.WaitForExitAsync(timeoutMs, cancellationToken)
                        .ConfigureAwait(false);

                    if (!exited)
                    {
                        await runner.WaitAndKillAsync(2000).ConfigureAwait(false);
                        return false;
                    }

                    return runner.LastExitCode == 0;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(logPath)) File.Delete(logPath);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private static bool? _customFfmpegSupported;

        /// <summary>
        /// Runs the self check required by the custom ffmpeg option and logs a single line,
        /// mirroring the message the Pro build prints: "Custom ffmpeg supports vulkan+libplacebo: True".
        /// </summary>
        internal static bool CheckCustomFfmpeg(IFfmpegManager ffmpegManager, bool force = false)
        {
            if (!force && _customFfmpegSupported.HasValue) return _customFfmpegSupported.Value;

            var logger = Plugin.Instance?.Logger;
            var path = GetCustomFfmpegPath();

            if (path is null)
            {
                _customFfmpegSupported = false;

                var configured = Plugin.Instance?.GetPluginOptions().MediaInfoExtractOptions.CustomFfmpegPath;
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    logger?.Warn("CustomFfmpeg - File not found: " + configured);
                }

                return false;
            }

            var supported = false;

            try
            {
                var filters = RunAndCaptureAsync(ffmpegManager, path, "-hide_banner -filters",
                        "CustomFfmpegCheck", 20000, CancellationToken.None)
                    .GetAwaiter().GetResult();

                var hasLibplacebo = filters != null &&
                                    filters.IndexOf("libplacebo", StringComparison.OrdinalIgnoreCase) >= 0;

                var hasVulkan = RunAsync(ffmpegManager, path,
                        "-hide_banner -loglevel error -init_hw_device vulkan -f lavfi -i nullsrc -frames:v 1 -f null -",
                        "CustomFfmpegVulkan", 20000, CancellationToken.None)
                    .GetAwaiter().GetResult();

                supported = hasLibplacebo && hasVulkan;
            }
            catch (Exception e)
            {
                if (Plugin.Instance?.DebugMode == true)
                {
                    logger?.Debug(e.Message);
                    logger?.Debug(e.StackTrace);
                }
            }

            _customFfmpegSupported = supported;
            logger?.Info("Custom ffmpeg supports vulkan+libplacebo: " + supported);

            return supported;
        }

        private static List<string> Tokenize(string input)
        {
            var tokens = new List<string>();

            if (string.IsNullOrEmpty(input)) return tokens;

            var current = new StringBuilder();
            var inQuotes = false;

            foreach (var c in input)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }

                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0) tokens.Add(current.ToString());

            return tokens;
        }

        private static bool IsFlag(string token)
        {
            return token.Length > 1 && token[0] == '-' && !char.IsDigit(token[1]);
        }

        private static string QuoteToken(string token)
        {
            return token.IndexOf(' ') < 0 && token.IndexOf('"') < 0
                ? token
                : "\"" + token.Replace("\"", "\\\"") + "\"";
        }

        private static bool IsProbeExecutable(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath)) return false;

            var name = Path.GetFileNameWithoutExtension(executablePath) ?? string.Empty;

            return name.IndexOf("ffprobe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   string.Equals(name, "probe", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class FfmpegManagerWithArguments : IFfmpegManager
        {
            private readonly IFfmpegManager _inner;

            public FfmpegManagerWithArguments(IFfmpegManager inner)
            {
                _inner = inner;
            }

            public IFfmpegConfiguration FfmpegConfiguration => _inner.FfmpegConfiguration;

            public FfmpegCapabilities FfmpegCapabilities
            {
                get => _inner.FfmpegCapabilities;
                set => _inner.FfmpegCapabilities = value;
            }

            public IFfmpegRunner CreateFfMpegRunner(string name, string logFilePath)
            {
                return new FfmpegRunnerWithArguments(_inner.CreateFfMpegRunner(name, logFilePath));
            }

            public bool CanEncodeToAudioCodec(ReadOnlySpan<char> codec)
            {
                return _inner.CanEncodeToAudioCodec(codec);
            }

            public bool CanEncodeToSubtitleCodec(ReadOnlySpan<char> codec)
            {
                return _inner.CanEncodeToSubtitleCodec(codec);
            }

            public bool CanExtractSubtitles(string codec, EncodingContext encodingContext)
            {
                return _inner.CanExtractSubtitles(codec, encodingContext);
            }

            public bool SupportsSubtitleConversionTo(MediaSourceInfo mediaSource, MediaStream mediaStream,
                ReadOnlySpan<char> toCodec, EncodingContext context, SubtitleDeliveryMethod deliveryMethod)
            {
                return _inner.SupportsSubtitleConversionTo(mediaSource, mediaStream, toCodec, context,
                    deliveryMethod);
            }

            public bool RequiresSubtitleProcessing(MediaSourceInfo mediaSource, MediaStream mediaStream,
                EncodingContext context, SubtitleDeliveryMethod deliveryMethod)
            {
                return _inner.RequiresSubtitleProcessing(mediaSource, mediaStream, context, deliveryMethod);
            }
        }

        private sealed class FfmpegRunnerWithArguments : IFfmpegRunner
        {
            private readonly IFfmpegRunner _inner;

            public FfmpegRunnerWithArguments(IFfmpegRunner inner)
            {
                _inner = inner;
            }

            public string Name => _inner.Name;

            public int? LastExitCode => _inner.LastExitCode;

            public bool Run(string exeFileName, string commandLineArgs)
            {
                return _inner.Run(exeFileName, InjectFor(exeFileName, commandLineArgs));
            }

            public bool Run(string exeFileName, string commandLineArgs, string workingDirectory)
            {
                return _inner.Run(exeFileName, InjectFor(exeFileName, commandLineArgs), workingDirectory);
            }

            public Task<bool> WaitForExitAsync(int timeoutMs, CancellationToken cancellationToken)
            {
                return _inner.WaitForExitAsync(timeoutMs, cancellationToken);
            }

            public Task<bool> WaitAndKillAsync(int timeoutMs)
            {
                return _inner.WaitAndKillAsync(timeoutMs);
            }

            public void Start(string commandLineArgs)
            {
                _inner.Start(ApplyEncoderArguments(commandLineArgs));
            }

            public void LogOutput(LogSeverity logSeverity)
            {
                _inner.LogOutput(logSeverity);
            }

            public void Dispose()
            {
                _inner.Dispose();
            }

            private static string InjectFor(string exeFileName, string commandLineArgs)
            {
                return IsProbeExecutable(exeFileName)
                    ? ApplyProbeArguments(commandLineArgs)
                    : ApplyEncoderArguments(commandLineArgs);
            }
        }
    }
}
