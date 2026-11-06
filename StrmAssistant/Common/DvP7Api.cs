using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Detects Dolby Vision Profile 7 dual-layer (BL + EL) discs. A retail UHD Blu-ray does not
    /// put a DOVI descriptor on the enhancement layer: the EL is a second HEVC PID (0x1015) and
    /// the only signal is the FFmpeg 9 Dolby Vision stream group, which Emby's ProbeResult does
    /// not model and which ffprobe prints only with `-show_stream_groups`. Single-track remuxes
    /// (MKV/MP4) have no stream group but carry a DOVI configuration record with
    /// el_present_flag/bl_present_flag, so that record is used as a fallback.
    /// </summary>
    internal static class DvP7Api
    {
        private const string DolbyVisionGroupType = "Dolby Vision";
        private const string DoviConfigurationRecord = "DOVI configuration record";
        private const int BaseLayerPid = 0x1011;
        private const int EnhancementLayerPid = 0x1015;

        /// <summary>
        /// Runs the static ffprobe against the main title (an extracted/local m2ts or a
        /// "bluray:" image URL). Returns null when the static ffprobe is unavailable or the
        /// probe fails; callers must treat that as "not detected" and keep Emby's own result.
        /// </summary>
        internal static async Task<DvP7Info> DetectAsync(string inputPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(inputPath)) return null;

            var json = await CustomFfprobeApi.RunProbeJsonAsync(inputPath,
                "-show_streams -show_stream_groups",
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json)) return null;

            DvProbeDocument document;

            try
            {
                document = Plugin.Instance?.JsonSerializer?.DeserializeFromString<DvProbeDocument>(json);
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("DvP7 - JSON parse failed: " + e.Message);

                return null;
            }

            return document is null ? null : Evaluate(document);
        }

        /// <summary>
        /// Applies the detection rules to a parsed ffprobe document. Kept separate from the
        /// probe so the rules can be exercised against recorded ffprobe output.
        /// </summary>
        internal static DvP7Info Evaluate(DvProbeDocument document)
        {
            var result = new DvP7Info();

            if (document is null) return result;

            var group = document.stream_groups?.FirstOrDefault(g =>
                g != null && g.nb_streams == 2 && g.streams != null && g.streams.Length == 2 &&
                !string.IsNullOrEmpty(g.type) &&
                g.type.IndexOf(DolbyVisionGroupType, StringComparison.OrdinalIgnoreCase) >= 0 &&
                g.streams.All(IsHevcVideo));

            if (group != null)
            {
                var baseLayer = group.streams.FirstOrDefault(s => IsPid(s.id, BaseLayerPid));
                var enhancementLayer = group.streams.FirstOrDefault(s => IsPid(s.id, EnhancementLayerPid));

                if (baseLayer is null || enhancementLayer is null)
                {
                    // Some ffprobe builds expose the pair without the HDMV PIDs. The base layer
                    // is the full-resolution stream and the enhancement layer is the other one.
                    var ordered = group.streams
                        .OrderByDescending(s => (long)s.width * s.height)
                        .ToArray();

                    if (baseLayer is null) baseLayer = ordered.FirstOrDefault();
                    if (enhancementLayer is null) enhancementLayer = ordered.LastOrDefault();
                }

                if (baseLayer != null && enhancementLayer != null &&
                    !ReferenceEquals(baseLayer, enhancementLayer))
                {
                    result.IsDvP7 = true;
                    result.HasStreamGroup = true;
                    result.Evidence = "stream_group";
                    result.BaseLayerIndex = baseLayer.index;
                    result.BaseLayerId = baseLayer.id;
                    result.EnhancementLayerIndex = enhancementLayer.index;
                    result.EnhancementLayerId = enhancementLayer.id;

                    ReadSideData(result, enhancementLayer);

                    return result;
                }
            }

            // Single-track P7 (MKV/MP4, or a spec TS that still carries an EL descriptor):
            // the configuration record itself states that both layers are present.
            foreach (var stream in document.streams ?? Array.Empty<DvProbeStream>())
            {
                if (!IsHevcVideo(stream)) continue;

                var sideData = FindDoviConfiguration(stream.side_data_list);

                if (sideData is null || sideData.dv_profile != 7 ||
                    sideData.el_present_flag == 0 || sideData.bl_present_flag == 0)
                {
                    continue;
                }

                result.IsDvP7 = true;
                result.Evidence = "dovi_config_record";
                result.BaseLayerIndex = stream.index;
                result.BaseLayerId = stream.id;

                ReadSideData(result, stream);

                return result;
            }

            return result;
        }

        private static void ReadSideData(DvP7Info result, DvProbeStream stream)
        {
            var sideData = FindDoviConfiguration(stream?.side_data_list);

            if (sideData is null) return;

            result.DvProfile = sideData.dv_profile;
            result.DvLevel = sideData.dv_level;
            result.BlCompatibilityId = sideData.dv_bl_signal_compatibility_id;
            result.ElPresent = sideData.el_present_flag != 0;
            result.BlPresent = sideData.bl_present_flag != 0;
            result.RpuPresent = sideData.rpu_present_flag != 0;
        }

        private static DvSideData FindDoviConfiguration(DvSideData[] sideDataList)
        {
            return sideDataList?.FirstOrDefault(d =>
                d != null && !string.IsNullOrEmpty(d.side_data_type) &&
                d.side_data_type.IndexOf(DoviConfigurationRecord, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsHevcVideo(DvProbeStream stream)
        {
            return stream != null &&
                   string.Equals(stream.codec_name, "hevc", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(stream.codec_type, "video", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPid(string id, int pid)
        {
            if (string.IsNullOrEmpty(id)) return false;

            var value = id.Trim();

            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out var hex))
            {
                return hex == pid;
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                   number == pid;
        }
    }

    internal sealed class DvP7Info
    {
        public bool IsDvP7 { get; set; }
        public bool HasStreamGroup { get; set; }
        public string Evidence { get; set; }
        public int? BaseLayerIndex { get; set; }
        public string BaseLayerId { get; set; }
        public int? EnhancementLayerIndex { get; set; }
        public string EnhancementLayerId { get; set; }
        public int? DvProfile { get; set; }
        public int? DvLevel { get; set; }
        public int? BlCompatibilityId { get; set; }
        public bool? ElPresent { get; set; }
        public bool? BlPresent { get; set; }
        public bool? RpuPresent { get; set; }
    }

    /// <summary>
    /// Minimal shape of `ffprobe -print_format json -show_streams -show_stream_groups`.
    /// Property names are lower-case to match the ffprobe JSON field names exactly; Emby's
    /// serializer (ServiceStack.Text) maps properties, not fields.
    /// </summary>
    internal sealed class DvProbeDocument
    {
        public DvStreamGroup[] stream_groups { get; set; }
        public DvProbeStream[] streams { get; set; }
    }

    internal sealed class DvStreamGroup
    {
        public int index { get; set; }
        public string id { get; set; }
        public int nb_streams { get; set; }
        public string type { get; set; }
        public DvProbeStream[] streams { get; set; }
    }

    internal sealed class DvProbeStream
    {
        public int index { get; set; }
        public string id { get; set; }
        public string codec_name { get; set; }
        public string codec_type { get; set; }
        public int width { get; set; }
        public int height { get; set; }
        public DvSideData[] side_data_list { get; set; }
    }

    internal sealed class DvSideData
    {
        public string side_data_type { get; set; }
        public int dv_profile { get; set; }
        public int dv_level { get; set; }
        public int dv_bl_signal_compatibility_id { get; set; }
        public int rpu_present_flag { get; set; }
        public int el_present_flag { get; set; }
        public int bl_present_flag { get; set; }
    }
}
