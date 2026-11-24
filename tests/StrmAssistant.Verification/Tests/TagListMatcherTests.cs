using StrmAssistant.Common;
using System;
using System.Linq;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// The tag blacklist sits beside the keyword blacklist: exact tag match, trimmed and
    /// case-insensitive, comma/semicolon/newline separated. These pin the parse and match rules.
    /// </summary>
    internal static class TagListMatcherTests
    {
        public static void Run(TestRunner runner)
        {
            ParseSplitsAndTrims(runner);
            ExactCaseInsensitive(runner);
            NoSubstringMatch(runner);
            NoWildcardExpansion(runner);
            EmptyConfigMatchesNothing(runner);
        }

        private static void ParseSplitsAndTrims(TestRunner runner)
        {
            runner.Section("T1. Parse splits on , ; newline | and trims");

            var set = TagListMatcher.Parse("  no-info , skip; hidden\r\n|x ");

            runner.Check("no-info", set.Contains("no-info"));
            runner.Check("skip", set.Contains("skip"));
            runner.Check("hidden", set.Contains("hidden"));
            runner.Check("x", set.Contains("x"));
            runner.Check("empty entries dropped", set.Count == 4);
        }

        private static void ExactCaseInsensitive(TestRunner runner)
        {
            runner.Section("T2. Exact, case-insensitive match");

            runner.Check("same case", TagListMatcher.Matches(new[] { "NoInfo" }, "noinfo"));
            runner.Check("different case", TagListMatcher.Matches(new[] { "NOINFO" }, "noinfo"));
            runner.Check("tag padded with spaces", TagListMatcher.Matches(new[] { "  noinfo  " }, "noinfo"));
            runner.Check("configured padded", TagListMatcher.Matches(new[] { "noinfo" }, "  noinfo  "));
        }

        private static void NoSubstringMatch(TestRunner runner)
        {
            runner.Section("T3. A substring is not a match (unlike keywords)");

            runner.Check("noinfo-extra does not match noinfo",
                !TagListMatcher.Matches(new[] { "noinfo-extra" }, "noinfo"));
        }

        private static void NoWildcardExpansion(TestRunner runner)
        {
            runner.Section("T4. Wildcards are not expanded for tags");

            runner.Check("star is literal",
                !TagListMatcher.Matches(new[] { "noinfo" }, "no*"));
        }

        private static void EmptyConfigMatchesNothing(TestRunner runner)
        {
            runner.Section("T5. Empty configuration matches nothing");

            runner.Check("null config", !TagListMatcher.Matches(new[] { "any" }, null));
            runner.Check("empty config", !TagListMatcher.Matches(new[] { "any" }, ""));
            runner.Check("whitespace config", !TagListMatcher.Matches(new[] { "any" }, "   "));
            runner.Check("null tags", !TagListMatcher.Matches(null, "noinfo"));
            runner.Check("empty tags", !TagListMatcher.Matches(Array.Empty<string>(), "noinfo"));
        }
    }
}
