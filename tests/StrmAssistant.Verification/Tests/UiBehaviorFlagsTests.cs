using StrmAssistant.Common;
using System;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// The two backported UI behaviours used to be injected unconditionally. These pin the
    /// off-state to v1.0.16 (the legacy regex and token classes verbatim) and the on-state to
    /// the extended DV/HDR/Atmos/DTS:X kinds, plus both default values.
    /// </summary>
    internal static class UiBehaviorFlagsTests
    {
        // Exactly the pattern shipped in v1.0.16 (41227b8), kept here as the off-state contract.
        private const string V1016HighlightPattern =
            @"\b(?:2160p|1080p|720p|576p|480p|8k|4k|uhd|blu-?ray|bdrip|brrip|remux|web-?dl|webdl|webrip|hdtv|dvdrip|hdrip|amzn|nf|dsnp|atvp|hmax|h\s*26[45]|h26[45]|hevc|avc|x26[45]|av1|vp9|mpeg-?2|10-?bit|8-?bit|hdr10\+?|hdr|dolby\s+vision|dv|dts(?:-?hd)?(?:\s+ma)?|truehd|atmos|ddp?|eac3|ac3|aac|flac|opus)\b";

        public static void Run(TestRunner runner)
        {
            DefaultsOff(runner);
            LegacyMatchesV1016(runner);
            ExtendedAddsKinds(runner);
            ShortcutMenuStateScript(runner);
        }

        private static void DefaultsOff(TestRunner runner)
        {
            runner.Section("U1. Both switches default to OFF");

            runner.Check("shortcut menu extended commands default is false",
                !UiBehaviorFlags.ShortcutMenuExtendedCommandsDefault);
            runner.Check("version naming extended tokens default is false",
                !UiBehaviorFlags.VersionNamingExtendedTokensDefault);
        }

        private static void LegacyMatchesV1016(TestRunner runner)
        {
            runner.Section("U2. Off state reproduces the v1.0.16 script fragments");

            runner.EqualString("legacy highlight pattern is the 1.0.16 literal",
                V1016HighlightPattern, UiBehaviorFlags.LegacyVersionHighlightPattern);
            runner.EqualString("VersionHighlightPattern(false) selects the legacy literal",
                V1016HighlightPattern, UiBehaviorFlags.VersionHighlightPattern(false));

            var legacy = UiBehaviorFlags.VersionTokenClassifier(false);

            runner.Check("legacy classifier keeps the hyphen-only normalisation",
                legacy.Contains(@".replace(/-/g, ' ')"));
            runner.Check("legacy classifier collapses dynamic range into codec",
                legacy.Contains("return 'codec';"));
            runner.Check("legacy classifier keeps Atmos/DTS in generic audio",
                legacy.Contains("return 'audio';"));
            runner.Check("legacy classifier has no dolby-vision kind",
                legacy.IndexOf("dolby-vision", StringComparison.Ordinal) < 0);
            runner.Check("legacy classifier has no hdr10plus kind",
                legacy.IndexOf("hdr10plus", StringComparison.Ordinal) < 0);
            runner.Check("legacy classifier has no atmos kind",
                legacy.IndexOf("return 'atmos'", StringComparison.Ordinal) < 0);
            runner.Check("legacy classifier has no dtsx kind",
                legacy.IndexOf("dtsx", StringComparison.Ordinal) < 0);
        }

        private static void ExtendedAddsKinds(TestRunner runner)
        {
            runner.Section("U3. On state adds the distinct DV/HDR/Atmos/DTS:X kinds");

            var extendedPattern = UiBehaviorFlags.VersionHighlightPattern(true);
            var extended = UiBehaviorFlags.VersionTokenClassifier(true);

            runner.Check("extended pattern differs from legacy",
                !string.Equals(extendedPattern, V1016HighlightPattern, StringComparison.Ordinal));
            runner.Check("extended pattern matches DTS:X / DTS-X",
                extendedPattern.Contains(@"dts[:\s-]?x"));
            runner.Check("extended pattern ends in a character lookahead so HDR10+ keeps the +",
                extendedPattern.EndsWith(@"(?![a-z0-9])", StringComparison.Ordinal));
            runner.Check("extended classifier normalises the colon",
                extended.Contains(@".replace(/[-:]/g, ' ')"));
            runner.Check("extended classifier preserves '+' when trimming",
                extended.Contains(@"[^a-z0-9+]"));
            runner.Check("extended classifier returns dolby-vision",
                extended.Contains("return 'dolby-vision';"));
            runner.Check("extended classifier returns hdr10plus",
                extended.Contains("return 'hdr10plus';"));
            runner.Check("extended classifier returns hdr10",
                extended.Contains("return 'hdr10';"));
            runner.Check("extended classifier returns atmos",
                extended.Contains("return 'atmos';"));
            runner.Check("extended classifier returns dtsx",
                extended.Contains("return 'dtsx';"));
        }

        private static void ShortcutMenuStateScript(TestRunner runner)
        {
            runner.Section("U4. Shortcut menu flag is published per request");

            var off = UiBehaviorFlags.ShortcutMenuStateScript(false);
            var on = UiBehaviorFlags.ShortcutMenuStateScript(true);

            runner.Check("off script sets extended: false", off.Contains("extended: false"));
            runner.Check("off script does not enable the extension",
                off.IndexOf("extended: true", StringComparison.Ordinal) < 0);
            runner.Check("on script sets extended: true", on.Contains("extended: true"));
        }
    }
}
