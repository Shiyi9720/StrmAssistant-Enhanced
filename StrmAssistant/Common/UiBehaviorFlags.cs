namespace StrmAssistant.Common
{
    /// <summary>
    /// Defaults and script fragments for the two backported UI behaviours that used to be
    /// injected unconditionally. Both switches default to OFF, so with the switches off the
    /// served script behaves exactly like v1.0.16: no extra shortcut commands, no extra
    /// multi-select branch, and the original version-name token classification.
    /// </summary>
    public static class UiBehaviorFlags
    {
        /// <summary>Adds the three extra shortcut commands and multi-select execution.</summary>
        public const bool ShortcutMenuExtendedCommandsDefault = false;

        /// <summary>Colours DV / HDR10+ / HDR10 / Atmos / DTS:X distinctly in version names.</summary>
        public const bool VersionNamingExtendedTokensDefault = false;

        /// <summary>
        /// The v1.0.16 version-name highlight pattern, kept verbatim. A trailing \b is required
        /// here because it is the 1.0.16 behaviour, even though it cannot keep the '+' of HDR10+.
        /// </summary>
        public const string LegacyVersionHighlightPattern =
            @"\b(?:2160p|1080p|720p|576p|480p|8k|4k|uhd|blu-?ray|bdrip|brrip|remux|web-?dl|webdl|webrip|hdtv|dvdrip|hdrip|amzn|nf|dsnp|atvp|hmax|h\s*26[45]|h26[45]|hevc|avc|x26[45]|av1|vp9|mpeg-?2|10-?bit|8-?bit|hdr10\+?|hdr|dolby\s+vision|dv|dts(?:-?hd)?(?:\s+ma)?|truehd|atmos|ddp?|eac3|ac3|aac|flac|opus)\b";

        /// <summary>
        /// Extended pattern: ends in a lookahead instead of \b so HDR10+ keeps its '+', and
        /// matches DTS:X / DTS-X before the generic DTS branch.
        /// </summary>
        public const string ExtendedVersionHighlightPattern =
            @"\b(?:2160p|1080p|720p|576p|480p|8k|4k|uhd|blu-?ray|bdrip|brrip|remux|web-?dl|webdl|webrip|hdtv|dvdrip|hdrip|amzn|nf|dsnp|atvp|hmax|h\s*26[45]|h26[45]|hevc|avc|x26[45]|av1|vp9|mpeg-?2|10-?bit|8-?bit|hdr10\+?|hdr|dolby\s+vision|dv|dts[:\s-]?x|dts(?:-?hd)?(?:\s+ma)?|truehd|atmos|ddp?|eac3|ac3|aac|flac|opus)(?![a-z0-9])";

        /// <summary>
        /// v1.0.16 classifyToken body: DV / HDR collapse into 'codec', Atmos / DTS into 'audio'.
        /// </summary>
        public const string LegacyVersionTokenClassifier =
@"        var value = token.toLowerCase().replace(/-/g, ' ').replace(/\s+/g, ' ')
            .replace(/^[^a-z0-9]+|[^a-z0-9]+$/g, '');
        if (/^(?:2160p|1080p|720p|576p|480p|8k|4k|uhd)$/.test(value)) return 'resolution';
        if (/^(?:blu ray|bdrip|brrip|remux|web dl|webdl|webrip|hdtv|dvdrip|hdrip|amzn|nf|dsnp|atvp|hmax)$/.test(value)) return 'source';
        if (/^(?:h 26[45]|h26[45]|hevc|avc|x26[45]|av1|vp9|mpeg 2|10 bit|10bit|8 bit|8bit|hdr10\+?|hdr|dolby vision|dv)$/.test(value)) return 'codec';
        if (/^(?:dts|dts hd|dts hd ma|truehd|atmos|dd|ddp|eac3|ac3|aac|flac|opus)$/.test(value)) return 'audio';
        return '';";

        /// <summary>
        /// Extended classifyToken body: dynamic range and object-based audio get their own kinds
        /// so DV / HDR10+ / HDR10 / HDR and Atmos / DTS:X are visually distinct.
        /// </summary>
        public const string ExtendedVersionTokenClassifier =
@"        var value = token.toLowerCase().replace(/[-:]/g, ' ').replace(/\s+/g, ' ')
            .replace(/^[^a-z0-9+]+|[^a-z0-9+]+$/g, '');
        if (/^(?:2160p|1080p|720p|576p|480p|8k|4k|uhd)$/.test(value)) return 'resolution';
        if (/^(?:blu ray|bdrip|brrip|remux|web dl|webdl|webrip|hdtv|dvdrip|hdrip|amzn|nf|dsnp|atvp|hmax)$/.test(value)) return 'source';
        // Dynamic-range tokens get their own kinds so DV / HDR10+ / HDR10 / plain HDR are
        // visually distinct instead of all collapsing into 'codec'.
        if (/^(?:dolby vision|dv)$/.test(value)) return 'dolby-vision';
        if (/^hdr10\+$/.test(value)) return 'hdr10plus';
        if (/^hdr10$/.test(value)) return 'hdr10';
        if (/^hdr$/.test(value)) return 'hdr';
        if (/^(?:h 26[45]|h26[45]|hevc|avc|x26[45]|av1|vp9|mpeg 2|10 bit|10bit|8 bit|8bit)$/.test(value)) return 'codec';
        // Object-based audio gets its own kinds; other audio keeps the generic 'audio'.
        if (/^atmos$/.test(value)) return 'atmos';
        if (/^dts x$/.test(value)) return 'dtsx';
        if (/^(?:dts|dts hd|dts hd ma|truehd|dd|ddp|eac3|ac3|aac|flac|opus)$/.test(value)) return 'audio';
        return '';";

        public static string VersionHighlightPattern(bool extendedTokens)
        {
            return extendedTokens ? ExtendedVersionHighlightPattern : LegacyVersionHighlightPattern;
        }

        public static string VersionTokenClassifier(bool extendedTokens)
        {
            return extendedTokens ? ExtendedVersionTokenClassifier : LegacyVersionTokenClassifier;
        }

        /// <summary>
        /// Per-request flag consumed by the injected shortcut command source. It is emitted as a
        /// window variable so toggling the option does not need a server restart.
        /// </summary>
        public static string ShortcutMenuStateScript(bool extendedCommands)
        {
            return "\nwindow.strmAssistantShortcutMenu = { extended: " +
                   (extendedCommands ? "true" : "false") + " };\n";
        }
    }
}
