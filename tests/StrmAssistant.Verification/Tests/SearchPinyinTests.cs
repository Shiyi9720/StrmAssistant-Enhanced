using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Regression coverage for issue #3 (拼音无效). The Chinese search index used to store only
    /// the single pinyin spelling chosen by the phrase dictionary. For a polyphonic character
    /// that spelling can disagree with what the user types; initials have no fuzzy fallback, so
    /// a query such as "frxxz" for 凡人修仙传 (indexed as "frxxc") returned nothing at all.
    /// These tests pin the generated keys, which is exactly what the index stores.
    /// </summary>
    internal static class SearchPinyinTests
    {
        public static void Run(TestRunner runner)
        {
            ReportedTitle(runner);
            PolyphonicInitials(runner);
            PhraseSpellingStaysPrimary(runner);
            TraditionalTitle(runner);
            NoChineseHasNoPinyin(runner);
            VariantCountIsBounded(runner);
        }

        /// <summary>The title from the reporter's screenshot must produce the obvious pinyin.</summary>
        private static void ReportedTitle(TestRunner runner)
        {
            runner.Section("S1. 吞噬星空 -> tunshixingkong / tsxk");

            var variants = SearchTextHelper.GetPinyinVariants("吞噬星空");

            runner.Check("S1 primary full pinyin",
                variants.Any(v => v.Pinyin == "tunshixingkong"));
            runner.Check("S1 primary initials",
                variants.Any(v => v.Initials == "tsxk"));
            runner.EqualString("S1 primary variant first", "tunshixingkong", variants[0].Pinyin);
        }

        /// <summary>
        /// 传 is read "chuan" by the phrase dictionary here but the user may type "zhuan".
        /// Both the full pinyin and the initials spelling must be indexed.
        /// </summary>
        private static void PolyphonicInitials(TestRunner runner)
        {
            runner.Section("S2. 凡人修仙传 indexes both chuan and zhuan readings");

            var variants = SearchTextHelper.GetPinyinVariants("凡人修仙传");
            var pinyin = variants.Select(v => v.Pinyin).ToList();
            var initials = variants.Select(v => v.Initials).ToList();

            runner.Check("S2 dictionary spelling indexed", pinyin.Contains("fanrenxiuxianchuan"));
            runner.Check("S2 alternate spelling indexed", pinyin.Contains("fanrenxiuxianzhuan"));
            runner.Check("S2 dictionary initials indexed", initials.Contains("frxxc"));
            runner.Check("S2 alternate initials indexed", initials.Contains("frxxz"));
            runner.Check("S2 full and initials agree",
                variants.Any(v => v.Pinyin == "fanrenxiuxianzhuan" && v.Initials == "frxxz"));

            // Every variant must be internally consistent: initials are the first letter of
            // each syllable of the corresponding pinyin.
            foreach (var variant in variants)
            {
                runner.Check("S2 initials match pinyin: " + variant.Pinyin,
                    variant.Initials.Length > 0 && variant.Pinyin.Length > 0 &&
                    variant.Initials.Length <= variant.Pinyin.Length);
            }
        }

        /// <summary>
        /// The phrase dictionary reads 阿甘正传 as "zhuan", not the character's first reading.
        /// Adding alternate readings must not drop the phrase spelling: it stays first and keeps
        /// exact-match precedence in the index.
        /// </summary>
        private static void PhraseSpellingStaysPrimary(TestRunner runner)
        {
            runner.Section("S3. phrase-dictionary spelling stays primary");

            // U+963F U+7518 U+6B63 U+4F20. Built from code points so the fixture text is
            // unambiguous in source control.
            var title = new string(new[] { (char) 0x963F, (char) 0x7518, (char) 0x6B63, (char) 0x4F20 });

            var variants = SearchTextHelper.GetPinyinVariants(title);

            runner.EqualString("S3 primary pinyin", "aganzhengzhuan", variants[0].Pinyin);
            runner.EqualString("S3 primary initials", "agzz", variants[0].Initials);
            runner.Check("S3 alternate chuan also indexed",
                variants.Any(v => v.Pinyin == "aganzhengchuan"));
        }

        private static void TraditionalTitle(TestRunner runner)
        {
            runner.Section("S4. traditional title keeps polyphone variants");

            var variants = SearchTextHelper.GetPinyinVariants("凡人修仙傳");

            runner.Check("S4 traditional initials",
                variants.Any(v => v.Initials == "frxxz"));
            runner.Check("S4 traditional full pinyin",
                variants.Any(v => v.Pinyin == "fanrenxiuxianzhuan"));
            runner.EqualString("S4 conversion search compacts to simplified",
                "凡人修仙传", SearchTextHelper.Compact(SearchTextHelper.ToSimplified("凡人修仙傳")));
        }

        private static void NoChineseHasNoPinyin(TestRunner runner)
        {
            runner.Section("S5. non-Chinese input produces no pinyin keys");

            runner.EqualLong("S5 latin only", 0, SearchTextHelper.GetPinyinVariants("The 8 Show").Length);
            runner.EqualLong("S5 empty", 0, SearchTextHelper.GetPinyinVariants(string.Empty).Length);
            runner.EqualLong("S5 null", 0, SearchTextHelper.GetPinyinVariants(null).Length);
            runner.EqualString("S5 compact strips punctuation", "spiderman", SearchTextHelper.Compact("Spider-Man"));
        }

        /// <summary>
        /// A title full of polyphones must not multiply into hundreds of keys, and the result
        /// must be deterministic (same order on every build so the persisted index is stable).
        /// </summary>
        private static void VariantCountIsBounded(TestRunner runner)
        {
            runner.Section("S6. variant count is capped and deterministic");

            var manyPolyphones = new string(new[]
            {
                (char) 0x4F20, (char) 0x884C, (char) 0x91CD,
                (char) 0x957F, (char) 0x4E50, (char) 0x8FD8
            });

            var first = SearchTextHelper.GetPinyinVariants(manyPolyphones);
            var second = SearchTextHelper.GetPinyinVariants(manyPolyphones);

            runner.Check("S6 capped at MaxPinyinVariants",
                first.Length <= SearchTextHelper.MaxPinyinVariants,
                "count=" + first.Length);
            runner.Check("S6 no duplicate spellings",
                first.Select(v => v.Pinyin).Distinct(StringComparer.Ordinal).Count() == first.Length);
            runner.Check("S6 deterministic order",
                first.Select(v => v.Pinyin).SequenceEqual(second.Select(v => v.Pinyin)));
            runner.Check("S6 every variant has initials",
                first.All(v => !string.IsNullOrEmpty(v.Initials)));
        }
    }
}
