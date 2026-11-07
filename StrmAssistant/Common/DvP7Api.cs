using System;
using System.Collections.Generic;
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

        // A MEL carries a very small residual stream (around 2% of the base layer), while a
        // FEL carries a substantial one. Values between the two bands are left Unknown on
        // purpose: guessing from a borderline ratio would be worse than showing no suffix.
        private const double MelMaxRatio = 0.04;
        private const double FelMinRatio = 0.08;

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

            var result = document is null ? null : Evaluate(document);

            // Retail UHD Blu-ray streams normally have no per-stream bit_rate or size tags.
            // When the layer type is still Unknown and the badge option is enabled, measure a
            // small packet window so the MEL/FEL heuristic can work on real discs too.
            if (result?.IsDvP7 == true &&
                result.EnhancementLayerType == DvP7EnhancementLayer.Unknown &&
                result.BaseLayerIndex.HasValue && result.EnhancementLayerIndex.HasValue &&
                Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions
                    ?.EnableDvP7LayerBadge == true)
            {
                var sampleJson = await CustomFfprobeApi.RunProbeJsonAsync(inputPath,
                    "-show_entries packet=stream_index,size -read_intervals %+#300",
                    cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(sampleJson))
                {
                    try
                    {
                        var sample = Plugin.Instance?.JsonSerializer?
                            .DeserializeFromString<DvProbeDocument>(sampleJson);

                        ApplyPacketSample(result, sample);
                    }
                    catch (Exception e)
                    {
                        Plugin.Instance?.Logger?.Debug("DvP7 - Packet sample parse failed: " + e.Message);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Applies the EL/BL byte ratio from a short packet sample. This is the fallback for
        /// retail discs, whose streams rarely expose bit_rate or NUMBER_OF_BYTES metadata.
        /// </summary>
        internal static void ApplyPacketSample(DvP7Info result, DvProbeDocument sample)
        {
            if (result?.IsDvP7 != true ||
                result.EnhancementLayerType != DvP7EnhancementLayer.Unknown ||
                !result.BaseLayerIndex.HasValue || !result.EnhancementLayerIndex.HasValue ||
                sample?.packets is null || sample.packets.Length == 0)
            {
                return;
            }

            long baseBytes = 0;
            long enhancementBytes = 0;

            foreach (var packet in sample.packets)
            {
                if (packet is null || !TryParseLong(packet.size, out var size) || size <= 0) continue;

                if (packet.stream_index == result.BaseLayerIndex.Value)
                {
                    baseBytes += size;
                }
                else if (packet.stream_index == result.EnhancementLayerIndex.Value)
                {
                    enhancementBytes += size;
                }
            }

            if (baseBytes <= 0 || enhancementBytes <= 0) return;

            result.EnhancementLayerBitrateRatio = (double)enhancementBytes / baseBytes;
            result.EnhancementLayerEvidence = "stream_groups/packet_sample";
            ClassifyByRatio(result);
            TraceClassification(result);
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
                    ClassifyEnhancementLayer(result, baseLayer, enhancementLayer, "stream_groups");

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
                ClassifyEnhancementLayer(result, stream, null, "dovi_config_record");

                return result;
            }

            return result;
        }

        /// <summary>
        /// Classifies the enhancement layer as MEL or FEL. FFmpeg 9 stream groups prove that a
        /// second HEVC layer exists, but do not carry a MEL/FEL flag on retail UHD Blu-ray
        /// discs. An explicit MEL/FEL token in the EL profile or tags is used first; otherwise
        /// the EL bitrate (or byte count) relative to the BL is used as a conservative
        /// heuristic. No conclusive signal means Unknown.
        /// </summary>
        private static void ClassifyEnhancementLayer(DvP7Info result, DvProbeStream baseLayer,
            DvProbeStream enhancementLayer, string source)
        {
            result.EnhancementLayerProfile = enhancementLayer?.profile;

            var profileHint = ReadLayerHint(enhancementLayer);

            if (profileHint != DvP7EnhancementLayer.Unknown)
            {
                result.EnhancementLayerType = profileHint;
                result.EnhancementLayerEvidence = source + "/profile";
                TraceClassification(result);
                return;
            }

            if (enhancementLayer is null)
            {
                result.EnhancementLayerType = DvP7EnhancementLayer.Unknown;
                result.EnhancementLayerEvidence = source + "/unknown";
                TraceClassification(result);
                return;
            }

            if (TryGetRate(baseLayer, out var baseRate, out var baseRateSource) &&
                TryGetRate(enhancementLayer, out var enhancementRate, out _) &&
                baseRate > 0 && enhancementRate > 0)
            {
                result.EnhancementLayerBitrateRatio = (double)enhancementRate / baseRate;
                result.EnhancementLayerEvidence = source + "/" + baseRateSource;
            }
            else if (TryGetByteCount(baseLayer, out var baseBytes) &&
                     TryGetByteCount(enhancementLayer, out var enhancementBytes) &&
                     baseBytes > 0 && enhancementBytes > 0)
            {
                result.EnhancementLayerBitrateRatio = (double)enhancementBytes / baseBytes;
                result.EnhancementLayerEvidence = source + "/size";
            }
            else
            {
                result.EnhancementLayerType = DvP7EnhancementLayer.Unknown;
                result.EnhancementLayerEvidence = source + "/unknown";
                TraceClassification(result);
                return;
            }

            ClassifyByRatio(result);

            TraceClassification(result);
        }

        private static void ClassifyByRatio(DvP7Info result)
        {
            if (!result.EnhancementLayerBitrateRatio.HasValue) return;

            if (result.EnhancementLayerBitrateRatio <= MelMaxRatio)
            {
                result.EnhancementLayerType = DvP7EnhancementLayer.Mel;
            }
            else if (result.EnhancementLayerBitrateRatio >= FelMinRatio)
            {
                result.EnhancementLayerType = DvP7EnhancementLayer.Fel;
            }
            else
            {
                result.EnhancementLayerType = DvP7EnhancementLayer.Unknown;
            }
        }

        private static DvP7EnhancementLayer ReadLayerHint(DvProbeStream stream)
        {
            if (stream is null) return DvP7EnhancementLayer.Unknown;

            var values = new List<string>();

            if (!string.IsNullOrWhiteSpace(stream.profile)) values.Add(stream.profile);
            if (!string.IsNullOrWhiteSpace(stream.codec_tag_string)) values.Add(stream.codec_tag_string);

            if (stream.tags != null)
            {
                foreach (var tag in stream.tags)
                {
                    if (string.IsNullOrEmpty(tag.Key) || string.IsNullOrEmpty(tag.Value)) continue;
                    if (!IsLayerHintTag(tag.Key)) continue;

                    values.Add(tag.Value);
                }
            }

            foreach (var value in values)
            {
                if (value is null) continue;

                if (value.IndexOf("full enhancement", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ContainsLayerToken(value, "fel"))
                {
                    return DvP7EnhancementLayer.Fel;
                }

                if (value.IndexOf("minimum enhancement", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ContainsLayerToken(value, "mel"))
                {
                    return DvP7EnhancementLayer.Mel;
                }
            }

            return DvP7EnhancementLayer.Unknown;
        }

        private static bool IsLayerHintTag(string key)
        {
            return string.Equals(key, "title", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "comment", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "handler_name", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "description", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "profile", StringComparison.OrdinalIgnoreCase) ||
                   key.IndexOf("layer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsLayerToken(string value, string token)
        {
            var parts = value.Split(new[] { ' ', '_', '-', '/', '(', ')', '[', ']', ':', ';', ',', '.' },
                StringSplitOptions.RemoveEmptyEntries);

            return parts.Any(part => string.Equals(part, token, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryGetRate(DvProbeStream stream, out long rate, out string source)
        {
            rate = 0;
            source = null;

            if (TryParseLong(stream?.bit_rate, out var bitRate) && bitRate > 0)
            {
                rate = bitRate;
                source = "bitrate";
                return true;
            }

            if (stream?.tags is null) return false;

            foreach (var tag in stream.tags)
            {
                if (!IsTagKey(tag.Key, "BPS")) continue;
                if (!TryParseLong(tag.Value, out var bps) || bps <= 0) continue;

                rate = bps;
                source = "bitrate";
                return true;
            }

            return false;
        }

        private static bool TryGetByteCount(DvProbeStream stream, out long bytes)
        {
            bytes = 0;

            if (stream?.tags is null) return false;

            foreach (var tag in stream.tags)
            {
                if (!IsTagKey(tag.Key, "NUMBER_OF_BYTES")) continue;
                if (!TryParseLong(tag.Value, out var value) || value <= 0) continue;

                bytes = value;
                return true;
            }

            return false;
        }

        private static bool IsTagKey(string key, string name)
        {
            return !string.IsNullOrEmpty(key) &&
                   (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryParseLong(string value, out long result)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static void TraceClassification(DvP7Info result)
        {
            var ratio = result.EnhancementLayerBitrateRatio.HasValue
                ? result.EnhancementLayerBitrateRatio.Value.ToString("P2", CultureInfo.InvariantCulture)
                : "n/a";

            Plugin.Instance?.Logger?.Debug("DvP7 - TRACE layer=" + result.EnhancementLayerType +
                                           ", evidence=" + result.EnhancementLayerEvidence +
                                           ", EL profile=" + (result.EnhancementLayerProfile ?? "unknown") +
                                           ", EL/BL ratio=" + ratio);
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
        public DvP7EnhancementLayer EnhancementLayerType { get; set; } = DvP7EnhancementLayer.Unknown;
        public string EnhancementLayerEvidence { get; set; }
        public string EnhancementLayerProfile { get; set; }
        public double? EnhancementLayerBitrateRatio { get; set; }

        public string GetEnhancementLayerBadge()
        {
            switch (EnhancementLayerType)
            {
                case DvP7EnhancementLayer.Fel:
                    return "Dolby Vision P7 (FEL)";
                case DvP7EnhancementLayer.Mel:
                    return "Dolby Vision P7 (MEL)";
                default:
                    return "Dolby Vision P7";
            }
        }
    }

    internal enum DvP7EnhancementLayer
    {
        Unknown,
        Mel,
        Fel
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
        public DvProbePacket[] packets { get; set; }
    }

    internal sealed class DvProbePacket
    {
        public int stream_index { get; set; }
        public string size { get; set; }
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
        public string codec_tag_string { get; set; }
        public string profile { get; set; }
        public string bit_rate { get; set; }
        public int width { get; set; }
        public int height { get; set; }
        public Dictionary<string, string> tags { get; set; }
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
