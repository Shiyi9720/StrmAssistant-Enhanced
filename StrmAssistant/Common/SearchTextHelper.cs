using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        /// <summary>
        /// Upper bound for the pinyin spellings stored per title. Polyphonic characters are
        /// indexed with their alternate readings as well, but the combination count is capped
        /// so a long title full of polyphones cannot multiply the index size without limit.
        /// </summary>
        internal const int MaxPinyinVariants = 16;

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

        private static readonly ConcurrentDictionary<string, PinyinVariant[]> PinyinVariantCache =
            new ConcurrentDictionary<string, PinyinVariant[]>(StringComparer.Ordinal);

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
                catch (Exception e)
                {
                    if (LogLimiter.ShouldLog("ChineseSearch - Simplified conversion", TimeSpan.FromMinutes(5)))
                    {
                        Plugin.Instance?.Logger?.Debug("ChineseSearch - Simplified conversion failed" +
                                                       Environment.NewLine + e);
                    }
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
                catch (Exception e)
                {
                    if (LogLimiter.ShouldLog("ChineseSearch - Traditional conversion", TimeSpan.FromMinutes(5)))
                    {
                        Plugin.Instance?.Logger?.Debug("ChineseSearch - Traditional conversion failed" +
                                                       Environment.NewLine + e);
                    }
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
                catch (Exception e)
                {
                    if (LogLimiter.ShouldLog("ChineseSearch - Pinyin conversion", TimeSpan.FromMinutes(5)))
                    {
                        Plugin.Instance?.Logger?.Debug("ChineseSearch - Pinyin conversion failed" +
                                                       Environment.NewLine + e);
                    }
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
                catch (Exception e)
                {
                    if (LogLimiter.ShouldLog("ChineseSearch - Initials conversion", TimeSpan.FromMinutes(5)))
                    {
                        Plugin.Instance?.Logger?.Debug("ChineseSearch - Initials conversion failed" +
                                                       Environment.NewLine + e);
                    }
                    return string.Empty;
                }
            });
        }

        /// <summary>
        /// One pinyin spelling of a title: the full pinyin and its initials.
        /// </summary>
        public sealed class PinyinVariant
        {
            public string Pinyin { get; set; }
            public string Initials { get; set; }
        }

        /// <summary>
        /// Pinyin spellings for a title, primary (phrase dictionary) spelling first.
        /// ToolGood.Words resolves a polyphonic character from the surrounding phrase, so the
        /// same title can be spelled differently depending on the word: 凡人修仙传 becomes
        /// "fanrenxiuxianchuan" while 阿甘正传 becomes "aganzhengzhuan". A user who types the
        /// other valid reading (e.g. "fanrenxiuxianzhuan" or the initials "frxxz") used to get
        /// nothing for initials, because initials never fell back to a fuzzy tier. Index every
        /// alternate reading instead, bounded by <see cref="MaxPinyinVariants"/>.
        /// </summary>
        public static PinyinVariant[] GetPinyinVariants(string input)
        {
            if (string.IsNullOrEmpty(input) || !HasChinese(input)) return Array.Empty<PinyinVariant>();

            return CachedConvert(PinyinVariantCache, input, BuildPinyinVariants);
        }

        private static PinyinVariant[] BuildPinyinVariants(string input)
        {
            var result = new List<PinyinVariant>(4);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string pinyin, string initials)
            {
                if (string.IsNullOrEmpty(pinyin) || !seen.Add(pinyin)) return;

                result.Add(new PinyinVariant { Pinyin = pinyin, Initials = initials });
            }

            // Keep the phrase-dictionary spelling first: it is what the index produced before
            // alternate readings were added, so existing exact matches keep their precedence.
            Add(ToPinyin(input), ToPinyinInitials(input));

            var parts = new List<List<string>>(input.Length);

            foreach (var c in input)
            {
                if (!IsCjk(c))
                {
                    if (char.IsLetterOrDigit(c)) parts.Add(new List<string> { c.ToString() });
                    continue;
                }

                var readings = new List<string>(2);

                try
                {
                    foreach (var reading in WordsHelper.GetAllPinyin(c, false))
                    {
                        var compact = Compact(reading);

                        if (compact.Length > 0 && !readings.Contains(compact)) readings.Add(compact);
                    }
                }
                catch (Exception e)
                {
                    // expected: fall through to the single-reading conversion below.
                    if (LogLimiter.ShouldLog("ChineseSearch - Polyphone expansion", TimeSpan.FromMinutes(5)))
                    {
                        Plugin.Instance?.Logger?.Debug("ChineseSearch - Polyphone expansion failed" +
                                                       Environment.NewLine + e);
                    }
                }

                if (readings.Count == 0)
                {
                    var reading = Compact(ToPinyin(c.ToString()));

                    if (reading.Length > 0) readings.Add(reading);
                }

                if (readings.Count > 0) parts.Add(readings);
            }

            if (parts.Count == 0) return result.ToArray();

            long combinations = 1;

            foreach (var part in parts)
            {
                combinations *= part.Count;

                if (combinations > MaxPinyinVariants) break;
            }

            if (combinations <= MaxPinyinVariants)
            {
                var indices = new int[parts.Count];

                while (true)
                {
                    if (result.Count >= MaxPinyinVariants) break;

                    var pinyin = new StringBuilder(input.Length + 8);
                    var initials = new StringBuilder(parts.Count);

                    for (var i = 0; i < parts.Count; i++)
                    {
                        var reading = parts[i][indices[i]];
                        pinyin.Append(reading);
                        initials.Append(reading[0]);
                    }

                    Add(pinyin.ToString(), initials.ToString());

                    var position = parts.Count - 1;

                    while (position >= 0)
                    {
                        if (++indices[position] < parts[position].Count) break;

                        indices[position] = 0;
                        position--;
                    }

                    if (position < 0) break;
                }
            }
            else
            {
                // Too many combinations: keep the primary spelling plus one alternate reading
                // at a time. That still covers the usual single-polyphone title cheaply.
                for (var position = 0; position < parts.Count && result.Count < MaxPinyinVariants;
                     position++)
                {
                    for (var reading = 1; reading < parts[position].Count &&
                                           result.Count < MaxPinyinVariants; reading++)
                    {
                        var pinyin = new StringBuilder(input.Length + 8);
                        var initials = new StringBuilder(parts.Count);

                        for (var i = 0; i < parts.Count; i++)
                        {
                            var value = parts[i][i == position ? reading : 0];
                            pinyin.Append(value);
                            initials.Append(value[0]);
                        }

                        Add(pinyin.ToString(), initials.ToString());
                    }
                }
            }

            return result.ToArray();
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

            TrimCache(cache);

            cache[input] = value;

            return value;
        }

        private static T[] CachedConvert<T>(ConcurrentDictionary<string, T[]> cache, string input,
            Func<string, T[]> converter)
        {
            if (cache.TryGetValue(input, out var cached)) return cached;

            var value = converter(input);

            TrimCache(cache);

            cache[input] = value;

            return value;
        }

        private static void TrimCache<T>(ConcurrentDictionary<string, T> cache)
        {
            if ((++_cacheOperations & CacheTrimCheckMask) == 0 && cache.Count > MaxCacheSize)
            {
                cache.Clear();
            }
        }
    }
}
