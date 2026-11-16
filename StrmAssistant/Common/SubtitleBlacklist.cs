using MediaBrowser.Model.Entities;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
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

        private sealed class Pattern
        {
            public string Text;
            public Regex Wildcard;
        }

        private static readonly ConcurrentDictionary<string, SubtitleBlacklist> Cache =
            new ConcurrentDictionary<string, SubtitleBlacklist>(StringComparer.Ordinal);

        private readonly List<Pattern> _languages;
        private readonly List<Pattern> _titles;
        private readonly List<Pattern> _codecs;

        private SubtitleBlacklist(List<Pattern> languages, List<Pattern> titles, List<Pattern> codecs)
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
            var languages = options.SubtitleBlacklistLanguages ?? string.Empty;
            var titles = options.SubtitleBlacklistTitles ?? string.Empty;
            var codecs = options.SubtitleBlacklistCodecs ?? string.Empty;
            var key = languages + "\u0001" + titles + "\u0001" + codecs;

            // The parsed patterns (including compiled wildcard regexes) are shared by every item
            // and media source. A saved option change produces a new key automatically.
            return Cache.GetOrAdd(key, _ => new SubtitleBlacklist(Split(languages), Split(titles),
                Split(codecs)));
        }

        /// <summary>
        /// Drops the shared parsed-pattern cache after options are saved.
        /// </summary>
        public static void InvalidateCache()
        {
            Cache.Clear();
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

            return _titles.Any(pattern => title.IndexOf(pattern.Text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          MatchesWildcard(title, pattern));
        }

        private bool MatchesCodec(MediaStream stream)
        {
            if (_codecs.Count == 0 || string.IsNullOrWhiteSpace(stream.Codec)) return false;

            return _codecs.Any(pattern =>
                MatchesExact(stream.Codec, pattern) || MatchesWildcard(stream.Codec, pattern));
        }

        private static List<Pattern> Split(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? new List<Pattern>()
                : value.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries)
                    .Select(i => i.Trim())
                    .Where(i => i.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(i => new Pattern
                    {
                        Text = i,
                        Wildcard = i.IndexOf('*') < 0 && i.IndexOf('?') < 0
                            ? null
                            : new Regex(
                                "^" + Regex.Escape(i).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
                                RegexOptions.Compiled)
                    })
                    .ToList();
        }

        private static string NormalizeLanguage(string language)
        {
            var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();

            return LanguageAliases.TryGetValue(normalized, out var alias) ? alias : normalized;
        }

        private static bool MatchesExact(string value, Pattern pattern)
        {
            if (pattern.Wildcard != null) return false;

            return string.Equals(value, pattern.Text, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesWildcard(string value, Pattern pattern)
        {
            return pattern.Wildcard?.IsMatch(value) ?? false;
        }
    }
}
