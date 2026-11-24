using StrmAssistant.Common;
using System;
using System.Collections.Generic;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Douban guest-cast localization matches an existing latin name to the Chinese name the
    /// celebrities payload carries. Whitespace and case must not defeat the match, and a credit
    /// whose latin name is already its name must not create a pointless rename.
    /// </summary>
    internal static class DoubanCastRulesTests
    {
        public static void Run(TestRunner runner)
        {
            Normalize(runner);
            Map(runner);
            Resolve(runner);
        }

        private static void Normalize(TestRunner runner)
        {
            runner.Section("C1. Name normalization collapses whitespace and case");

            runner.Check("case", DoubanCastRules.NormalizeName("Tom Hanks") == "tom hanks");
            runner.Check("extra spaces", DoubanCastRules.NormalizeName("  Tom   Hanks  ") == "tom hanks");
            runner.Check("null", DoubanCastRules.NormalizeName(null) == string.Empty);
            runner.Check("blank", DoubanCastRules.NormalizeName("   ") == string.Empty);
        }

        private static void Map(TestRunner runner)
        {
            runner.Section("C2. Latin map keeps only meaningful Chinese names");

            var map = DoubanCastRules.BuildLatinNameMap(new[]
            {
                ("汤姆·汉克斯", "Tom Hanks"),
                ("Tom Hanks", "Tom Hanks"),
                ("", "Nobody"),
                ("无拉丁名", null)
            });

            runner.Check("chinese mapping", map.Count == 1 && map["tom hanks"] == "汤姆·汉克斯");
        }

        private static void Resolve(TestRunner runner)
        {
            runner.Section("C3. Resolve returns the Chinese name only on a real change");

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tom hanks"] = "汤姆·汉克斯"
            };

            runner.Check("renames latin", DoubanCastRules.TryResolveChineseName("Tom Hanks", map,
                out var chinese) && chinese == "汤姆·汉克斯");
            runner.Check("same whitespace still matches", DoubanCastRules.TryResolveChineseName("  tom   hanks ",
                map, out var chinese2) && chinese2 == "汤姆·汉克斯");
            runner.Check("already chinese", !DoubanCastRules.TryResolveChineseName("汤姆·汉克斯", map,
                out _));
            runner.Check("unknown", !DoubanCastRules.TryResolveChineseName("Someone Else", map, out _));
            runner.Check("empty map", !DoubanCastRules.TryResolveChineseName("Tom Hanks", null, out _));
        }
    }
}
