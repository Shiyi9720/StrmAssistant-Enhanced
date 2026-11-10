using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Exercises DvP7Api.DetectAsync end to end: the real static ffprobe binary is started by the
    /// FfmpegApi shim (real process, real stdout/stderr), so CustomFfprobeApi.RunProbeJsonAsync
    /// and the DvP7 rules run their production code. When no ffprobe can be found the whole group
    /// is reported as SKIP, never as a failure.
    /// </summary>
    internal static class DvP7DetectTests
    {
        private static readonly string[] FfprobeCandidates =
        {
            "/vol2/1000/docker/emby/config/plugins/configurations/StrmAssistant/bin/ffprobe",
            "/config/plugins/configurations/StrmAssistant/bin/ffprobe"
        };

        private static readonly string[] SampleCandidates =
        {
            "/vol02/1000-1-cdb0a16a/媒体机VS零售机【Geekerwan】2026.mp4"
        };

        public static void Run(TestRunner runner)
        {
            runner.Section("D0. environment");

            var ffprobe = ResolveFfprobe();

            if (ffprobe is null)
            {
                runner.Skip("DvP7 DetectAsync group",
                    "no static ffprobe found (set STRMASSISTANT_FFPROBE)");
                return;
            }

            Console.WriteLine("      ffprobe: " + ffprobe);
            Configure(ffprobe);

            var workDir = Path.Combine(Path.GetTempPath(),
                "strmassistant-verify-dvp7-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            var wav = WriteWav(Path.Combine(workDir, "tone.wav"));
            var empty = Path.Combine(workDir, "empty.bin");
            File.WriteAllBytes(empty, Array.Empty<byte>());

            try
            {
                NormalProbe(runner, wav);
                NonZeroExit(runner, workDir);
                EmptyOutput(runner, workDir, empty);
                Timeout(runner, wav, workDir);
                PacketSampleFallback(runner, ffprobe, workDir);
            }
            finally
            {
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

        private static void NormalProbe(TestRunner runner, string wav)
        {
            runner.Section("D1. normal probe: real ffprobe, valid JSON, parsed successfully");

            FfmpegApi.Reset();

            DvP7Info info = null;
            Exception error = null;

            try
            {
                info = DvP7Api.DetectAsync(wav, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex;
            }

            runner.Check("D1 DetectAsync does not throw", error is null,
                error?.GetType().Name + ": " + error?.Message);
            runner.Check("D1 returns a parsed result (not null)", info != null);

            if (info is null) return;

            runner.Check("D1 WAV is not Dolby Vision P7", !info.IsDvP7,
                "Evidence=" + (info.Evidence ?? "<null>"));
            runner.Check("D1 no stream group", !info.HasStreamGroup);
            runner.Check("D1 enhancement layer stays Unknown",
                info.EnhancementLayerType == DvP7EnhancementLayer.Unknown,
                "actual " + info.EnhancementLayerType);

            var probe = FfmpegApi.Last;

            runner.Check("D1 ffprobe was really executed", probe != null);
            if (probe is null) return;

            runner.Check("D1 probe used -show_streams -show_stream_groups",
                probe.Mentions("-show_streams -show_stream_groups"), probe.Arguments);
            runner.EqualLong("D1 probe exit code", 0, probe.ExitCode ?? int.MinValue);
            runner.Check("D1 probe output contains a JSON object",
                probe.Stdout != null && probe.Stdout.Contains("\"streams\""),
                "stdout length " + (probe.Stdout?.Length ?? 0));
        }

        private static void NonZeroExit(TestRunner runner, string workDir)
        {
            runner.Section("D2. ffprobe exits non-zero (missing input)");

            var missing = Path.Combine(workDir, "does-not-exist.mkv");

            FfmpegApi.Reset();

            var json = CustomFfprobeApi.RunProbeJsonAsync(missing,
                "-show_streams -show_stream_groups", CancellationToken.None).GetAwaiter().GetResult();

            var probe = FfmpegApi.Last;

            runner.Check("D2 ffprobe was executed", probe != null);
            if (probe != null)
            {
                runner.Check("D2 exit code is non-zero", probe.ExitCode.HasValue && probe.ExitCode != 0,
                    "actual " + (probe.ExitCode?.ToString() ?? "<none>"));
            }

            // ffprobe still prints an empty JSON object with -print_format json, so the failure
            // surfaces as an empty document rather than null. Pin that actual behaviour.
            runner.Check("D2 failed probe yields null or an empty JSON object",
                json is null || IsEmptyJsonObject(json),
                "actual '" + (json ?? "<null>") + "'");

            var info = DvP7Api.DetectAsync(missing, CancellationToken.None).GetAwaiter().GetResult();

            runner.Check("D2 DetectAsync degrades to a non-P7 result without throwing",
                info != null && !info.IsDvP7,
                info is null ? "result was null" : "IsDvP7=" + info.IsDvP7);

            // A probe that fails without printing anything must return null instead.
            var failedWrapper = WriteWrapper(workDir, "ffprobe-fail-empty",
                "exit 1\n");

            var failedJson = ProbeThroughWrapper(workDir, failedWrapper);

            runner.Check("D2b failed probe with no output returns null", failedJson is null,
                "actual '" + (failedJson ?? "<null>") + "'");
        }

        private static void EmptyOutput(TestRunner runner, string workDir, string emptyFile)
        {
            runner.Section("D3. ffprobe produces empty or non-JSON output");

            // The real binary prints "{}" even for an unreadable file, so this branch is driven
            // with a wrapper that emits exactly what the branch handles.
            var silentWrapper = WriteWrapper(workDir, "ffprobe-silent", "exit 0\n");
            var silentJson = ProbeThroughWrapper(workDir, silentWrapper);

            runner.Check("D3 empty stdout returns null", silentJson is null,
                "actual '" + (silentJson ?? "<null>") + "'");

            var junkWrapper = WriteWrapper(workDir, "ffprobe-junk",
                "printf 'probe exploded\\n'\nexit 1\n");
            var junkJson = ProbeThroughWrapper(workDir, junkWrapper);

            runner.Check("D3 non-JSON output returns null (no JSON object to slice)", junkJson is null,
                "actual '" + (junkJson ?? "<null>") + "'");

            // The empty file itself is kept as evidence of real-binary behaviour: it exits
            // non-zero but still prints "{}".
            FfmpegApi.Reset();
            var realJson = CustomFfprobeApi.RunProbeJsonAsync(emptyFile,
                "-show_streams -show_stream_groups", CancellationToken.None).GetAwaiter().GetResult();

            runner.Check("D3 real empty file still yields an empty JSON object or null",
                realJson is null || IsEmptyJsonObject(realJson),
                "actual '" + (realJson ?? "<null>") + "'");
            runner.Check("D3 real empty file exits non-zero",
                FfmpegApi.Last != null && FfmpegApi.Last.ExitCode.HasValue &&
                FfmpegApi.Last.ExitCode != 0,
                "actual " + (FfmpegApi.Last?.ExitCode?.ToString() ?? "<none>"));
        }

        private static string ProbeThroughWrapper(string workDir, string wrapper)
        {
            var sample = Path.Combine(workDir, "probe-input.mkv");
            File.WriteAllBytes(sample, Array.Empty<byte>());

            var options = Plugin.Instance.Options.MediaInfoExtractOptions;
            var previous = options.CustomFfprobePath;

            options.CustomFfprobePath = wrapper;

            try
            {
                return CustomFfprobeApi.RunProbeJsonAsync(sample,
                    "-show_streams -show_stream_groups", CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                options.CustomFfprobePath = previous;
            }
        }

        private static string WriteWrapper(string directory, string name, string body)
        {
            var path = Path.Combine(directory, name);

            File.WriteAllText(path,
                "#!/bin/sh\n" +
                "case \"$*\" in\n" +
                "  *-version*) printf 'ffprobe version 9.0.2-verify\\n'; exit 0 ;;\n" +
                "esac\n" +
                body);

            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            return path;
        }

        private static void Timeout(TestRunner runner, string wav, string workDir)
        {
            runner.Section("D4. probe timeout (real ffprobe blocking on a FIFO, 500 ms budget)");

            // A FIFO with no writer makes ffprobe block in open(); a real timeout is then
            // deterministic instead of racing a tiny WAV against a millisecond budget.
            var fifo = Path.Combine(workDir, "blocking.fifo");
            using (var mkfifo = Process.Start(new ProcessStartInfo
                   {
                       FileName = "mkfifo",
                       Arguments = "\"" + fifo + "\"",
                       UseShellExecute = false
                   }))
            {
                mkfifo.WaitForExit();
            }

            FfmpegApi.Reset();

            var json = CustomFfprobeApi.RunProbeJsonAsync(fifo,
                "-show_streams -show_stream_groups", 500, CancellationToken.None)
                .GetAwaiter().GetResult();

            runner.Check("D4 timed-out probe returns null", json is null,
                "actual '" + (json ?? "<null>") + "'");

            var probe = FfmpegApi.Last;

            runner.Check("D4 ffprobe was executed", probe != null);
            if (probe != null)
            {
                runner.Check("D4 process hit the timeout", probe.TimedOut);
            }

            runner.Check("D4 DetectAsync with a cancelled token returns null",
                DvP7Api.DetectAsync(wav, new CancellationToken(true)).GetAwaiter().GetResult() is null);
        }

        private static void PacketSampleFallback(TestRunner runner, string ffprobe, string workDir)
        {
            runner.Section("D5. -read_intervals %+#300 packet-sample fallback");

            var sample = ResolveSample();

            if (sample is null)
            {
                runner.Skip("D5 packet-sample fallback",
                    "no multi-stream media sample found (set STRMASSISTANT_SAMPLE)");
                return;
            }

            Console.WriteLine("      sample: " + sample);

            // Run the same packet probe the plugin will run, so the expected ratio comes from
            // real ffprobe output rather than a hard-coded number.
            var packetArguments = "-v error -print_format json -show_entries packet=stream_index,size " +
                                  "-read_intervals %+#300 \"" + sample + "\"";
            var packetJson = RunProcess(ffprobe, packetArguments);

            long baseBytes = 0;
            long enhancementBytes = 0;

            using (var document = JsonDocument.Parse(packetJson))
            {
                foreach (var packet in document.RootElement.GetProperty("packets").EnumerateArray())
                {
                    var index = packet.GetProperty("stream_index").GetInt32();
                    var size = long.Parse(packet.GetProperty("size").GetString(),
                        CultureInfo.InvariantCulture);

                    if (index == 0) baseBytes += size;
                    else if (index == 1) enhancementBytes += size;
                }
            }

            if (baseBytes <= 0 || enhancementBytes <= 0)
            {
                runner.Skip("D5 packet-sample fallback", "sample did not yield packets for streams 0 and 1");
                return;
            }

            var expectedRatio = (double) enhancementBytes / baseBytes;
            var expectedType = Classify(expectedRatio);

            // The wrapper passes the real binary through for the packet probe, so only the
            // stream-group document (which no local sample can produce) is synthesized.
            var fakeJson = Path.Combine(workDir, "stream-group-unknown.json");
            File.WriteAllText(fakeJson, StreamGroupUnknownJson());

            var wrapper = Path.Combine(workDir, "ffprobe-wrapper");
            File.WriteAllText(wrapper, WrapperScript(ffprobe, fakeJson));
            File.SetUnixFileMode(wrapper,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var options = Plugin.Instance.Options.MediaInfoExtractOptions;
            options.CustomFfprobePath = wrapper;
            options.EnableDvP7LayerBadge = true;

            FfmpegApi.Reset();

            DvP7Info info = null;

            try
            {
                info = DvP7Api.DetectAsync(sample, CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                options.EnableDvP7LayerBadge = false;
                options.CustomFfprobePath = ffprobe;
            }

            runner.Check("D5 DetectAsync returns a result", info != null);
            if (info is null) return;

            runner.Check("D5 detected as P7", info.IsDvP7);
            runner.Check("D5 stream group evidence", info.HasStreamGroup);
            runner.EqualString("D5 primary evidence", "stream_group", info.Evidence);
            runner.EqualString("D5 packet-sample evidence", "stream_groups/packet_sample",
                info.EnhancementLayerEvidence);
            runner.Check("D5 ratio matches real packet bytes",
                info.EnhancementLayerBitrateRatio.HasValue &&
                Math.Abs(info.EnhancementLayerBitrateRatio.Value - expectedRatio) < 1e-9,
                "expected " + expectedRatio.ToString("F9", CultureInfo.InvariantCulture) +
                ", actual " + (info.EnhancementLayerBitrateRatio?.ToString("F9",
                    CultureInfo.InvariantCulture) ?? "<null>"));
            runner.Check("D5 classification matches the ratio", info.EnhancementLayerType == expectedType,
                "expected " + expectedType + ", actual " + info.EnhancementLayerType +
                ", ratio " + expectedRatio.ToString("F6", CultureInfo.InvariantCulture));

            var invocations = FfmpegApi.Invocations.ToList();

            runner.Check("D5 a second probe used -read_intervals %+#300",
                invocations.Any(i => i.Mentions("-read_intervals %+#300")),
                "invocations: " + string.Join(" | ", invocations.Select(i => i.Arguments)));
            runner.Check("D5 wrapper validated and invoked at least three times",
                invocations.Count >= 3, "actual " + invocations.Count);
        }

        private static bool IsEmptyJsonObject(string json)
        {
            if (json is null) return false;

            return new string(json.Where(c => !char.IsWhiteSpace(c)).ToArray()) == "{}";
        }

        private static DvP7EnhancementLayer Classify(double ratio)
        {
            if (ratio <= 0.04) return DvP7EnhancementLayer.Mel;
            if (ratio >= 0.08) return DvP7EnhancementLayer.Fel;

            return DvP7EnhancementLayer.Unknown;
        }

        private static string StreamGroupUnknownJson()
        {
            return """
            {
              "stream_groups": [
                {
                  "index": 0,
                  "id": "0x1",
                  "nb_streams": 2,
                  "type": "Dolby Vision",
                  "streams": [
                    {
                      "index": 0,
                      "id": "0x1011",
                      "codec_name": "hevc",
                      "codec_type": "video",
                      "profile": "Main 10",
                      "width": 3840,
                      "height": 2160
                    },
                    {
                      "index": 1,
                      "id": "0x1015",
                      "codec_name": "hevc",
                      "codec_type": "video",
                      "profile": "Main 10",
                      "width": 1920,
                      "height": 1080
                    }
                  ]
                }
              ],
              "streams": []
            }
            """;
        }

        private static string WrapperScript(string realFfprobe, string fakeJson)
        {
            return "#!/bin/sh\n" +
                   "case \"$*\" in\n" +
                   "  *-version*) printf 'ffprobe version 9.0.2-verify\\n'; exit 0 ;;\n" +
                   "esac\n" +
                   "case \"$*\" in\n" +
                   "  *-read_intervals*) exec '" + realFfprobe.Replace("'", "'\\''") + "' \"$@\" ;;\n" +
                   "esac\n" +
                   "cat '" + fakeJson.Replace("'", "'\\''") + "'\n";
        }

        private static string RunProcess(string exePath, string arguments)
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

            using var process = Process.Start(startInfo);

            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            return stdout;
        }

        private static string WriteWav(string path)
        {
            const int sampleRate = 8000;
            const short channels = 1;
            const short bitsPerSample = 16;
            const int seconds = 1;
            var dataLength = sampleRate * seconds * channels * bitsPerSample / 8;

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short) 1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bitsPerSample / 8);
            writer.Write((short) (channels * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            writer.Write(new byte[dataLength]);

            return path;
        }

        private static void Configure(string ffprobe)
        {
            Plugin.Instance.Logger = new ConsoleLogger("verify");
            Plugin.Instance.FfmpegManager = new DummyFfmpegManager();
            Plugin.Instance.ApplicationPaths.PluginConfigurationsPath =
                Path.Combine(Path.GetTempPath(), "strmassistant-verify-config");
            Plugin.Instance.DataFolderPath = Plugin.Instance.ApplicationPaths.PluginConfigurationsPath;
            Directory.CreateDirectory(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath);

            var options = Plugin.Instance.Options.MediaInfoExtractOptions;
            options.CustomFfprobePath = ffprobe;
            options.EnableDvP7LayerBadge = false;
        }

        private static string ResolveFfprobe()
        {
            var environment = Environment.GetEnvironmentVariable("STRMASSISTANT_FFPROBE");

            var candidates = new List<string> {environment};
            candidates.AddRange(FfprobeCandidates);

            return candidates.FirstOrDefault(path =>
                !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        }

        private static string ResolveSample()
        {
            var environment = Environment.GetEnvironmentVariable("STRMASSISTANT_SAMPLE");

            var candidates = new List<string> {environment};
            candidates.AddRange(SampleCandidates);

            return candidates.FirstOrDefault(path =>
                !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        }

        private sealed class DummyFfmpegManager : MediaBrowser.Controller.MediaEncoding.IFfmpegManager
        {
        }
    }
}
