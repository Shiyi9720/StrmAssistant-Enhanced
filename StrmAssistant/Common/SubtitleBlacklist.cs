using MediaBrowser.Model.Entities;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Matches subtitle streams against the user configured language/title/codec blacklist.
    /// A matched subtitle stream is removed when media info is persisted.
    /// </summary>
    public sealed class SubtitleBlacklist
    {
        private static readonly char[] ListSeparators = { ',', ';', '\r', '\n', '|' };

        private static readonly Dictionary<string, string> LanguageAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["eng"] = "en",
                ["chi"] = "zh",
                ["zho"] = "zh",
                ["zhtw"] = "zh-tw",
                ["jpn"] = "ja",
                ["kor"] = "ko",
                ["fra"] = "fr",
                ["fre"] = "fr",
                ["deu"] = "de",
                ["ger"] = "de",
                ["spa"] = "es",
                ["ita"] = "it",
                ["rus"] = "ru",
                ["por"] = "pt",
                ["tha"] = "th",
                ["vie"] = "vi",
                ["ara"] = "ar",
                ["hin"] = "hi",
                ["pol"] = "pl",
                ["tur"] = "tr",
                ["nld"] = "nl",
                ["dut"] = "nl",
                ["swe"] = "sv",
                ["nor"] = "no",
                ["dan"] = "da",
                ["fin"] = "fi",
                ["ces"] = "cs",
                ["cze"] = "cs",
                ["hun"] = "hu",
                ["ell"] = "el",
                ["gre"] = "el",
                ["heb"] = "he",
                ["ukr"] = "uk",
                ["ron"] = "ro",
                ["rum"] = "ro",
                ["ind"] = "id",
                ["msa"] = "ms",
                ["may"] = "ms"
            };

        private readonly List<string> _languages;
        private readonly List<string> _titles;
        private readonly List<string> _codecs;

        private SubtitleBlacklist(List<string> languages, List<string> titles, List<string> codecs)
        {
            _languages = languages;
            _titles = titles;
            _codecs = codecs;
        }

        public bool HasAny => _languages.Count > 0 || _titles.Count > 0 || _codecs.Count > 0;

        public static bool IsConfigured
        {
            get
            {
                var options = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions;

                return !string.IsNullOrWhiteSpace(options.SubtitleBlacklistLanguages) ||
                       !string.IsNullOrWhiteSpace(options.SubtitleBlacklistTitles) ||
                       !string.IsNullOrWhiteSpace(options.SubtitleBlacklistCodecs);
            }
        }

        public static SubtitleBlacklist FromOptions()
        {
            var options = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions;

            return new SubtitleBlacklist(Split(options.SubtitleBlacklistLanguages), Split(options.SubtitleBlacklistTitles),
                Split(options.SubtitleBlacklistCodecs));
        }

        public bool IsBlacklisted(MediaStream stream)
        {
            if (stream is null || stream.Type != MediaStreamType.Subtitle) return false;

            return MatchesLanguage(stream) || MatchesTitle(stream) || MatchesCodec(stream);
        }

        private bool MatchesLanguage(MediaStream stream)
        {
            if (_languages.Count == 0 || string.IsNullOrWhiteSpace(stream.Language)) return false;

            var raw = stream.Language.Trim();
            var normalized = NormalizeLanguage(raw);

            return _languages.Any(pattern =>
                MatchesExact(raw, pattern) || MatchesExact(normalized, pattern) ||
                MatchesWildcard(raw, pattern) || MatchesWildcard(normalized, pattern));
        }

        private bool MatchesTitle(MediaStream stream)
        {
            if (_titles.Count == 0) return false;

            var title = stream.Title;
            if (string.IsNullOrWhiteSpace(title)) title = stream.DisplayTitle;
            if (string.IsNullOrWhiteSpace(title)) title = stream.Comment;
            if (string.IsNullOrWhiteSpace(title)) return false;

            return _titles.Any(pattern => title.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          MatchesWildcard(title, pattern));
        }

        private bool MatchesCodec(MediaStream stream)
        {
            if (_codecs.Count == 0 || string.IsNullOrWhiteSpace(stream.Codec)) return false;

            return _codecs.Any(pattern =>
                MatchesExact(stream.Codec, pattern) || MatchesWildcard(stream.Codec, pattern));
        }

        private static List<string> Split(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries)
                    .Select(i => i.Trim())
                    .Where(i => i.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        private static string NormalizeLanguage(string language)
        {
            var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();

            return LanguageAliases.TryGetValue(normalized, out var alias) ? alias : normalized;
        }

        private static bool MatchesExact(string value, string pattern)
        {
            if (pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0) return false;

            return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesWildcard(string value, string pattern)
        {
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0) return false;

            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";

            return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
