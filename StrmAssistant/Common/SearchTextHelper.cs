using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using ToolGood.Words.Pinyin;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Text preparation shared by the search index and the query side. Everything is reduced to
    /// lower case letters, digits and CJK characters so that punctuation, full width forms and
    /// spacing in a title never stop a match.
    /// </summary>
    internal static class SearchTextHelper
    {
        private const int MaxCacheSize = 200000;
        private const int CacheTrimCheckMask = 1023;

        private static readonly Regex CjkRegex =
            new Regex("[\\u3400-\\u4DBF\\u4E00-\\u9FFF]", RegexOptions.Compiled);

        private static readonly ConcurrentDictionary<string, string> SimplifiedCache =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, string> TraditionalCache =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, string> PinyinCache =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, string> InitialsCache =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static int _cacheOperations;

        public static bool HasChinese(string input)
        {
            return !string.IsNullOrEmpty(input) && CjkRegex.IsMatch(input);
        }

        public static bool IsCjk(char c)
        {
            return (c >= '\u3400' && c <= '\u4DBF') ||
                   (c >= '\u4E00' && c <= '\u9FFF') ||
                   (c >= '\uF900' && c <= '\uFAFF');
        }

        /// <summary>
        /// CJK characters only. Mixed titles such as "Spider-Man 蜘蛛侠" also get a pinyin key for
        /// the Chinese part alone, so searching "zhizhuxia" or "zzx" still finds them.
        /// </summary>
        public static string ExtractChinese(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            var builder = new StringBuilder(input.Length);

            foreach (var c in input)
            {
                if (IsCjk(c)) builder.Append(c);
            }

            return builder.ToString();
        }

        public static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            return input.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Lower case alphanumeric + CJK projection. Used for every index key and query so that
        /// "Spider-Man", "Spider Man" and "spiderman" collapse to the same value.
        /// </summary>
        public static string Compact(string input)
        {
            var normalized = Normalize(input);

            if (normalized.Length == 0) return string.Empty;

            var builder = new StringBuilder(normalized.Length);

            foreach (var c in normalized)
            {
                if (char.IsLetterOrDigit(c)) builder.Append(c);
            }

            return builder.ToString();
        }

        public static string ToSimplified(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            return CachedConvert(SimplifiedCache, input, s =>
            {
                try
                {
                    return LanguageUtility.ConvertTraditionalToSimplified(s) ?? s;
                }
                catch
                {
                    return s;
                }
            });
        }

        public static string ToTraditional(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            return CachedConvert(TraditionalCache, input, s =>
            {
                try
                {
                    return LanguageUtility.ConvertSimplifiedToTraditional(s) ?? s;
                }
                catch
                {
                    return s;
                }
            });
        }

        public static string ToPinyin(string input)
        {
            if (string.IsNullOrEmpty(input) || !HasChinese(input)) return string.Empty;

            return CachedConvert(PinyinCache, input, s =>
            {
                try
                {
                    return Compact(WordsHelper.GetPinyin(s, false));
                }
                catch
                {
                    return string.Empty;
                }
            });
        }

        public static string ToPinyinInitials(string input)
        {
            if (string.IsNullOrEmpty(input) || !HasChinese(input)) return string.Empty;

            return CachedConvert(InitialsCache, input, s =>
            {
                try
                {
                    return Compact(WordsHelper.GetFirstPinyin(s));
                }
                catch
                {
                    return string.Empty;
                }
            });
        }

        /// <summary>
        /// True when <paramref name="query"/>'s characters appear in <paramref name="value"/>
        /// in the same order. This is the cheap fuzzy tier (abbreviations and omitted letters).
        /// </summary>
        public static bool IsSubsequence(string query, string value)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(value) || query.Length > value.Length)
            {
                return false;
            }

            var index = 0;

            foreach (var c in value)
            {
                if (c == query[index])
                {
                    index++;
                    if (index == query.Length) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Bounded edit distance test. Only used for keys that start with the same character,
        /// with a hard distance cap, so a typo search never turns into a full quadratic scan.
        /// </summary>
        public static bool IsSimilar(string a, string b, int maxDistance)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (Math.Abs(a.Length - b.Length) > maxDistance) return false;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];

            for (var j = 0; j <= b.Length; j++) previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                var rowMinimum = current[0];

                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + cost);

                    if (current[j] < rowMinimum) rowMinimum = current[j];
                }

                if (rowMinimum > maxDistance) return false;

                var swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length] <= maxDistance;
        }

        private static string CachedConvert(ConcurrentDictionary<string, string> cache, string input,
            Func<string, string> converter)
        {
            if (cache.TryGetValue(input, out var cached)) return cached;

            var value = converter(input);

            if ((++_cacheOperations & CacheTrimCheckMask) == 0 && cache.Count > MaxCacheSize)
            {
                cache.Clear();
            }

            cache[input] = value;

            return value;
        }
    }
}
