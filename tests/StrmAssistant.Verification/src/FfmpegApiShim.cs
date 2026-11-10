// Test-side replacement for StrmAssistant.Common.FfmpegApi.RunAndCaptureAsync.
//
// The real implementation delegates to Emby's IFfmpegManager runner, which only exists inside a
// running Emby server. This shim performs the same job with System.Diagnostics.Process: start the
// executable, capture its output, honour the timeout and cancellation token, kill it when either
// fires. It is NOT a mock of the process call - the real static ffprobe binary is executed and its
// real stdout/stderr are returned, so CustomFfprobeApi.RunProbeJsonAsync and DvP7Api.DetectAsync
// run their production code paths end to end.

using MediaBrowser.Controller.MediaEncoding;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    internal static class FfmpegApi
    {
        private static readonly object Sync = new object();

        /// <summary>Every invocation recorded by the current test run, newest last.</summary>
        internal static readonly System.Collections.Generic.List<Invocation> Invocations =
            new System.Collections.Generic.List<Invocation>();

        internal static void Reset()
        {
            lock (Sync)
            {
                Invocations.Clear();
            }
        }

        internal static Invocation Last
        {
            get
            {
                lock (Sync)
                {
                    return Invocations.Count == 0 ? null : Invocations[Invocations.Count - 1];
                }
            }
        }

        internal static async Task<string> RunAndCaptureAsync(IFfmpegManager ffmpegManager,
            string exePath, string arguments, string name, int timeoutMs,
            CancellationToken cancellationToken)
        {
            var result = await RunAndCaptureWithExitCodeAsync(ffmpegManager, exePath, arguments, name,
                timeoutMs, cancellationToken).ConfigureAwait(false);

            return result.Output;
        }

        internal static async Task<RunResult> RunAndCaptureWithExitCodeAsync(IFfmpegManager ffmpegManager,
            string exePath, string arguments, string name, int timeoutMs,
            CancellationToken cancellationToken)
        {
            if (ffmpegManager is null || string.IsNullOrEmpty(exePath)) return RunResult.Empty;

            var invocation = new Invocation(exePath, arguments);
            var effectiveTimeout = timeoutMs > 0 ? timeoutMs : 120000;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = startInfo };

                if (!process.Start())
                {
                    invocation.StartFailed = true;
                    return RunResult.Empty;
                }

                // Read both pipes concurrently so a chatty process cannot deadlock on a full pipe.
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                timeout.CancelAfter(effectiveTimeout);

                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    invocation.TimedOut = true;

                    try
                    {
                        if (!process.HasExited) process.Kill(true);
                    }
                    catch
                    {
                        // ignored
                    }

                    try
                    {
                        await process.WaitForExitAsync(new CancellationTokenSource(2000).Token)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignored
                    }

                    return RunResult.Empty;
                }

                invocation.Stdout = await stdoutTask.ConfigureAwait(false);
                invocation.Stderr = await stderrTask.ConfigureAwait(false);
                invocation.ExitCode = process.ExitCode;

                var builder = new StringBuilder();
                builder.Append(invocation.Stdout);
                builder.Append(invocation.Stderr);

                return new RunResult(builder.ToString(), invocation.ExitCode);
            }
            catch (Exception e)
            {
                invocation.Error = e.GetType().Name + ": " + e.Message;
                return RunResult.Empty;
            }
            finally
            {
                lock (Sync)
                {
                    Invocations.Add(invocation);
                }
            }
        }

        /// <summary>Mirror of the production FfmpegApi.RunResult shape.</summary>
        internal readonly struct RunResult
        {
            public static readonly RunResult Empty = new RunResult(null, null);

            public RunResult(string output, int? exitCode)
            {
                Output = output;
                ExitCode = exitCode;
            }

            public string Output { get; }

            public int? ExitCode { get; }
        }

        internal sealed class Invocation
        {
            public Invocation(string exePath, string arguments)
            {
                ExePath = exePath;
                Arguments = arguments;
            }

            public string ExePath { get; }
            public string Arguments { get; }
            public int? ExitCode { get; set; }
            public string Stdout { get; set; }
            public string Stderr { get; set; }
            public string Error { get; set; }
            public bool TimedOut { get; set; }
            public bool StartFailed { get; set; }

            public bool Mentions(string token)
            {
                return Arguments != null &&
                       Arguments.IndexOf(token, StringComparison.Ordinal) >= 0;
            }
        }
    }
}
