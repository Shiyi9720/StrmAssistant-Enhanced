using StrmAssistant.Common;
using System;
using System.IO;

namespace StrmAssistant.Verification
{
    internal static class DvP7Tests
    {
        public static void Run(TestRunner runner)
        {
            RecordedSingleTrackP7(runner);
            RecordedProfile8(runner);
            SyntheticStreamGroups(runner);
            PlainHevc(runner);
            NullDocument(runner);
        }

        private static void RecordedSingleTrackP7(TestRunner runner)
        {
            runner.Section("B1. p7-single-track.json (recorded ffprobe output)");

            var document = Parse(ReadFixture("p7-single-track.json"));
            runner.Check("B1 fixture parsed", document != null);

            var info = DvP7Api.Evaluate(document);

            runner.Check("B1 IsDvP7 is true", info.IsDvP7);
            runner.EqualString("B1 Evidence", "dovi_config_record", info.Evidence);
            runner.Check("B1 BaseLayerIndex is 0", info.BaseLayerIndex == 0,
                "actual " + Format(info.BaseLayerIndex));
            runner.Check("B1 EnhancementLayerType is Unknown",
                info.EnhancementLayerType == DvP7EnhancementLayer.Unknown,
                "actual " + info.EnhancementLayerType);
            runner.EqualString("B1 EnhancementLayerEvidence", "dovi_config_record/unknown",
                info.EnhancementLayerEvidence);
            runner.Check("B1 DvProfile is 7", info.DvProfile == 7, "actual " + Format(info.DvProfile));
            runner.Check("B1 ElPresent is true", info.ElPresent == true,
                "actual " + Format(info.ElPresent));
            runner.Check("B1 BlPresent is true", info.BlPresent == true,
                "actual " + Format(info.BlPresent));
        }

        private static void RecordedProfile8(TestRunner runner)
        {
            runner.Section("B2. dv-profile8.json (recorded ffprobe output)");

            var document = Parse(ReadFixture("dv-profile8.json"));
            runner.Check("B2 fixture parsed", document != null);

            var info = DvP7Api.Evaluate(document);

            runner.Check("B2 IsDvP7 is false", !info.IsDvP7,
                "Evidence=" + (info.Evidence ?? "<null>") + ", DvProfile=" + Format(info.DvProfile));
        }

        private static void SyntheticStreamGroups(TestRunner runner)
        {
            runner.Section("B3. synthetic stream_groups MEL/FEL/Unknown");

            var mel = DvP7Api.Evaluate(Parse(StreamGroupJson(2_000_000L)));
            runner.Check("B3 MEL IsDvP7", mel.IsDvP7);
            runner.Check("B3 MEL HasStreamGroup", mel.HasStreamGroup);
            runner.EqualString("B3 MEL Evidence", "stream_group", mel.Evidence);
            runner.Check("B3 MEL ratio ~0.02", Near(mel.EnhancementLayerBitrateRatio, 0.02),
                "actual " + FormatRatio(mel.EnhancementLayerBitrateRatio));
            runner.Check("B3 MEL EnhancementLayerType is Mel",
                mel.EnhancementLayerType == DvP7EnhancementLayer.Mel,
                "actual " + mel.EnhancementLayerType);
            runner.EqualString("B3 MEL layer evidence", "stream_groups/bitrate",
                mel.EnhancementLayerEvidence);

            var fel = DvP7Api.Evaluate(Parse(StreamGroupJson(15_000_000L)));
            runner.Check("B3 FEL IsDvP7", fel.IsDvP7);
            runner.Check("B3 FEL ratio ~0.15", Near(fel.EnhancementLayerBitrateRatio, 0.15),
                "actual " + FormatRatio(fel.EnhancementLayerBitrateRatio));
            runner.Check("B3 FEL EnhancementLayerType is Fel",
                fel.EnhancementLayerType == DvP7EnhancementLayer.Fel,
                "actual " + fel.EnhancementLayerType);

            var unknown = DvP7Api.Evaluate(Parse(StreamGroupJson(6_000_000L)));
            runner.Check("B3 Unknown IsDvP7", unknown.IsDvP7);
            runner.Check("B3 Unknown ratio ~0.06", Near(unknown.EnhancementLayerBitrateRatio, 0.06),
                "actual " + FormatRatio(unknown.EnhancementLayerBitrateRatio));
            runner.Check("B3 Unknown EnhancementLayerType is Unknown",
                unknown.EnhancementLayerType == DvP7EnhancementLayer.Unknown,
                "actual " + unknown.EnhancementLayerType);
        }

        private static void PlainHevc(TestRunner runner)
        {
            runner.Section("B4. plain HEVC without stream_groups or DOVI record");

            const string json = """
            {
              "stream_groups": [],
              "streams": [
                {
                  "index": 0,
                  "codec_name": "hevc",
                  "codec_type": "video",
                  "profile": "Main 10",
                  "width": 1920,
                  "height": 1080,
                  "side_data_list": [
                    { "side_data_type": "HEVC enhancement-layer decoder configuration" }
                  ]
                }
              ]
            }
            """;

            var info = DvP7Api.Evaluate(Parse(json));

            runner.Check("B4 IsDvP7 is false", !info.IsDvP7,
                "Evidence=" + (info.Evidence ?? "<null>"));
        }

        private static void NullDocument(TestRunner runner)
        {
            runner.Section("B5. Evaluate(null)");

            DvP7Info info = null;
            var threw = false;

            try
            {
                info = DvP7Api.Evaluate(null);
            }
            catch (Exception ex)
            {
                threw = true;
                runner.Check("B5 Evaluate(null) does not throw", false,
                    ex.GetType().Name + ": " + ex.Message);
            }

            if (!threw) runner.Check("B5 Evaluate(null) does not throw", true);

            runner.Check("B5 null document yields IsDvP7=false", info != null && !info.IsDvP7,
                info is null ? "result was null" : "IsDvP7=true");
        }

        private static string StreamGroupJson(long enhancementLayerBps)
        {
            return $$"""
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
                      "height": 2160,
                      "tags": { "BPS": "100000000" }
                    },
                    {
                      "index": 1,
                      "id": "0x1015",
                      "codec_name": "hevc",
                      "codec_type": "video",
                      "profile": "Main 10",
                      "width": 1920,
                      "height": 1080,
                      "tags": { "BPS": "{{enhancementLayerBps}}" }
                    }
                  ]
                }
              ],
              "streams": []
            }
            """;
        }

        private static DvProbeDocument Parse(string json)
        {
            return Plugin.Instance.JsonSerializer.DeserializeFromString<DvProbeDocument>(json);
        }

        private static string ReadFixture(string name)
        {
            return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
        }

        private static bool Near(double? actual, double expected)
        {
            return actual.HasValue && Math.Abs(actual.Value - expected) < 0.000001;
        }

        private static string Format(int? value)
        {
            return value.HasValue ? value.Value.ToString() : "<null>";
        }

        private static string Format(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "<null>";
        }

        private static string FormatRatio(double? value)
        {
            return value.HasValue ? value.Value.ToString("F6") : "<null>";
        }
    }
}
