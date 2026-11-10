using StrmAssistant.Common;
using System;
using System.IO;
using System.Threading;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Group H: the ffprobe exit code is no longer dropped. Real ffprobe prints "{}" even when it
    /// fails, so a non-zero exit code with no stream data must return null, while a non-zero exit
    /// code that still produced streams is kept. Wrapper scripts stand in for the binary so each
    /// exit-code/output combination is deterministic and no real ffprobe is required.
    /// </summary>
    internal static class ProbeExitCodeTests
    {
        public static void Run(TestRunner runner)
        {
            var workDir = Path.Combine(Path.GetTempPath(),
                "strmassistant-verify-exitcode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            Plugin.Instance.Logger = Plugin.Instance.Logger ?? new ConsoleLogger("verify");
            Plugin.Instance.FfmpegManager = new DummyFfmpegManager();
            Plugin.Instance.ApplicationPaths.PluginConfigurationsPath =
                Path.Combine(workDir, "config");
            Plugin.Instance.DataFolderPath =
                Plugin.Instance.ApplicationPaths.PluginConfigurationsPath;
            Directory.CreateDirectory(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath);

            var input = Path.Combine(workDir, "input.mkv");
            File.WriteAllBytes(input, Array.Empty<byte>());

            var previousPath = Plugin.Instance.Options.MediaInfoExtractOptions.CustomFfprobePath;

            try
            {
                NonZeroEmptyObject(runner, workDir, input);
                ValidJsonExitZero(runner, workDir, input);
                EmptyObjectExitZero(runner, workDir, input);
                NonZeroWithStreams(runner, workDir, input);
            }
            finally
            {
                Plugin.Instance.Options.MediaInfoExtractOptions.CustomFfprobePath = previousPath;

                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private static void NonZeroEmptyObject(TestRunner runner, string workDir, string input)
        {
            runner.Section("H1. non-zero exit code + {} -> null");

            var json = Probe(runner, workDir, input, "h1", 1, "printf '{}\\n'");

            runner.Check("H1 returns null", json is null, "actual '" + (json ?? "<null>") + "'");
            CheckExitCode(runner, "H1", 1);
        }

        private static void ValidJsonExitZero(TestRunner runner, string workDir, string input)
        {
            runner.Section("H2. exit code 0 + valid JSON -> returned unchanged");

            const string payload = "{\"streams\":[{\"index\":0,\"codec_name\":\"hevc\"}],\"format\":{\"duration\":\"1.0\"}}";

            var json = Probe(runner, workDir, input, "h2", 0, "printf '%s\\n' '" + payload + "'");

            runner.EqualString("H2 JSON is returned unchanged", payload, json);
            CheckExitCode(runner, "H2", 0);
        }

        private static void EmptyObjectExitZero(TestRunner runner, string workDir, string input)
        {
            runner.Section("H3. exit code 0 + {} -> {}");

            var json = Probe(runner, workDir, input, "h3", 0, "printf '{}\\n'");

            runner.EqualString("H3 empty object is preserved", "{}", json);
            CheckExitCode(runner, "H3", 0);
        }

        private static void NonZeroWithStreams(TestRunner runner, string workDir, string input)
        {
            runner.Section("H4. non-zero exit code + valid streams -> JSON returned");

            const string payload = "{\"streams\":[{\"index\":0}],\"stream_groups\":[{\"type\":\"Dolby Vision\"}]}";

            var json = Probe(runner, workDir, input, "h4", 1, "printf '%s\\n' '" + payload + "'");

            runner.EqualString("H4 JSON with stream data is not discarded", payload, json);
            CheckExitCode(runner, "H4", 1);
        }

        private static string Probe(TestRunner runner, string workDir, string input, string name,
            int exitCode, string body)
        {
            var wrapper = WriteWrapper(workDir, name, exitCode, body);
            var options = Plugin.Instance.Options.MediaInfoExtractOptions;
            options.CustomFfprobePath = wrapper;

            FfmpegApi.Reset();

            var json = CustomFfprobeApi.RunProbeJsonAsync(input,
                "-show_streams -show_stream_groups", CancellationToken.None).GetAwaiter().GetResult();

            runner.Check(name + " wrapper executed", FfmpegApi.Last != null);

            return json;
        }

        private static void CheckExitCode(TestRunner runner, string prefix, int expected)
        {
            var probe = FfmpegApi.Last;

            runner.EqualLong(prefix + " observed exit code", expected,
                probe?.ExitCode ?? int.MinValue);
        }

        private static string WriteWrapper(string directory, string name, int exitCode, string body)
        {
            var path = Path.Combine(directory, name + "-ffprobe");

            File.WriteAllText(path,
                "#!/bin/sh\n" +
                "case \"$*\" in\n" +
                "  *-version*) printf 'ffprobe version 9.0.2-verify\\n'; exit 0 ;;\n" +
                "esac\n" +
                body + "\n" +
                "exit " + exitCode + "\n");

            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            return path;
        }

        private sealed class DummyFfmpegManager : MediaBrowser.Controller.MediaEncoding.IFfmpegManager
        {
        }
    }
}
