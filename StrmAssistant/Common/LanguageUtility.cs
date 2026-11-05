using Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter;
using System;
using System.Text.RegularExpressions;

namespace StrmAssistant.Common
{
    public static class LanguageUtility
    {
        private static readonly Regex EnglishRegex = new Regex(@"^[\x00-\x7F]+$", RegexOptions.Compiled);
        private static readonly Regex ChineseRegex = new Regex(@"[\u4E00-\u9FFF]", RegexOptions.Compiled);
        private static readonly Regex JapaneseRegex = new Regex(@"[\u3040-\u30FF]", RegexOptions.Compiled);
        private static readonly Regex KoreanRegex = new Regex(@"[\uAC00-\uD7A3]", RegexOptions.Compiled);
        private static readonly Regex DefaultEnglishEpisodeNameRegex = new Regex(@"^Episode\s*\d+$", RegexOptions.Compiled);
        private static readonly Regex DefaultChineseEpisodeNameRegex = new Regex(@"^第\s*\d+\s*集$", RegexOptions.Compiled);
        private static readonly Regex DefaultJapaneseEpisodeNameRegex = new Regex(@"^第\s*\d+\s*話$", RegexOptions.Compiled);
        private static readonly Regex DefaultChineseCollectionNameRegex = new Regex(@"（系列）$", RegexOptions.Compiled);
        private static readonly Regex CleanPersonNameRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex CleanEpisodeNameRegex =
            new Regex(@"(\.)(?:S[0-9]+[eE][0-9]+|[sS][0-9]+[xX][0-9]+|[sS][0-9]+[-_][0-9]+|第[0-9一二三四五六七八九十百]+集)?",
                RegexOptions.Compiled);

        public static readonly string[] MovieDbFallbackLanguages = { "zh-CN", "zh-SG", "zh-HK", "zh-TW", "ja-JP" };
        public static readonly string[] TvdbFallbackLanguages = { "zho", "zhtw", "yue", "jpn" };

        public static bool IsEnglish(string input) => !string.IsNullOrEmpty(input) && EnglishRegex.IsMatch(input);

        public static bool IsChineseLanguageCode(string language)
        {
            if (string.IsNullOrEmpty(language)) return false;

            var code = language.Trim().Replace('_', '-');

            return code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "zho", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "zhtw", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "yue", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTraditionalChineseLanguageCode(string language)
        {
            if (!IsChineseLanguageCode(language)) return false;

            var code = language.Trim().Replace('_', '-');

            return code.IndexOf("hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   code.EndsWith("-tw", StringComparison.OrdinalIgnoreCase) ||
                   code.EndsWith("-hk", StringComparison.OrdinalIgnoreCase) ||
                   code.EndsWith("-mo", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "zhtw", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "zh-hk", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSimplifiedChineseLanguageCode(string language)
        {
            if (!IsChineseLanguageCode(language) || IsTraditionalChineseLanguageCode(language)) return false;

            var code = language.Trim().Replace('_', '-');

            return code.IndexOf("hans", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   code.EndsWith("-cn", StringComparison.OrdinalIgnoreCase) ||
                   code.EndsWith("-sg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "zh", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "zho", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Image languages requested from TMDB for the given Chinese variant. Traditional
        /// Chinese content accepts both zh-TW and zh-HK posters/logos; simplified content
        /// only accepts zh-CN so the two sets stay apart.
        /// </summary>
        public static string GetChineseImageLanguages(string language)
        {
            if (IsTraditionalChineseLanguageCode(language)) return "zh-TW,zh-HK";
            if (IsSimplifiedChineseLanguageCode(language)) return "zh-CN";

            return null;
        }

        public static string GetPreferredChineseMetadataLanguage(bool preferTraditional)
        {
            return preferTraditional ? "zh-TW" : "zh-CN";
        }

        public static bool IsChinese(string input) => !string.IsNullOrEmpty(input) && ChineseRegex.IsMatch(input) &&
                                                      !JapaneseRegex.IsMatch(input.Replace("\u30FB", string.Empty));

        public static bool IsJapanese(string input) => !string.IsNullOrEmpty(input) &&
                                                       JapaneseRegex.IsMatch(input.Replace("\u30FB", string.Empty));

        public static bool IsChineseJapanese(string input) => !string.IsNullOrEmpty(input) &&
                                                              (ChineseRegex.IsMatch(input) ||
                                                               JapaneseRegex.IsMatch(input.Replace("\u30FB",
                                                                   string.Empty)));

        public static bool IsKorean(string input) => !string.IsNullOrEmpty(input) && KoreanRegex.IsMatch(input);
        
        public static bool IsDefaultEnglishEpisodeName(string input) =>
            !string.IsNullOrEmpty(input) && DefaultEnglishEpisodeNameRegex.IsMatch(input);

        public static bool IsDefaultChineseEpisodeName(string input) =>
            !string.IsNullOrEmpty(input) && DefaultChineseEpisodeNameRegex.IsMatch(input);

        public static bool IsDefaultJapaneseEpisodeName(string input) =>
            !string.IsNullOrEmpty(input) && DefaultJapaneseEpisodeNameRegex.IsMatch(input);

        public static string ConvertTraditionalToSimplified(string input)
        {
            return ChineseConverter.Convert(input, ChineseConversionDirection.TraditionalToSimplified);
        }

        public static string ConvertSimplifiedToTraditional(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            return ChineseConverter.Convert(input, ChineseConversionDirection.SimplifiedToTraditional);
        }

        public static string GetLanguageByTitle(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;

            return IsJapanese(input) ? "ja" : IsKorean(input) ? "ko" : IsChinese(input) ? "zh" : "en";
        }

        public static string RemoveDefaultCollectionName(string input)
        {
            return string.IsNullOrEmpty(input) ? input : DefaultChineseCollectionNameRegex.Replace(input, "").Trim();
        }

        public static string CleanPersonName(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            if (IsChineseJapanese(input) || IsKorean(input))
            {
                return CleanPersonNameRegex.Replace(input, "");
            }

            return input.Trim();
        }

        public static string CleanEpisodeName(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            return CleanEpisodeNameRegex.Replace(input, "");
        }
    }
}
