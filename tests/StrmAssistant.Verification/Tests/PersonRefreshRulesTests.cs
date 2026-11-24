using StrmAssistant.Common;
using System;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Enhanced person refresh: one TMDB request per actor, and only fields that need changing are
    /// written. These pin the grouping key and the overview-churn guard.
    /// </summary>
    internal static class PersonRefreshRulesTests
    {
        public static void Run(TestRunner runner)
        {
            GroupKey(runner);
            Names(runner);
            Overviews(runner);
            Dates(runner);
        }

        private static void GroupKey(TestRunner runner)
        {
            runner.Section("P1. Rows with the same TMDB id group together");

            runner.Check("same tmdb id", PersonRefreshRules.GroupKey(" 42 ", 1) ==
                                        PersonRefreshRules.GroupKey("42", 2));
            runner.Check("missing tmdb id falls back to row id",
                PersonRefreshRules.GroupKey(null, 7) == "id:7");
            runner.Check("blank tmdb id falls back to row id",
                PersonRefreshRules.GroupKey("  ", 7) == "id:7");
        }

        private static void Names(TestRunner runner)
        {
            runner.Section("P2. Name is written only when Chinese, unlocked and different");

            runner.Check("chinese different", PersonRefreshRules.ShouldApplyName("汤姆·汉克斯", "Tom Hanks", false, true));
            runner.Check("not chinese", !PersonRefreshRules.ShouldApplyName("Tom Hanks", "汤姆", false, false));
            runner.Check("locked", !PersonRefreshRules.ShouldApplyName("汤姆·汉克斯", "Tom Hanks", true, true));
            runner.Check("same value", !PersonRefreshRules.ShouldApplyName("汤姆", "汤姆", false, true));
            runner.Check("empty", !PersonRefreshRules.ShouldApplyName("", "Tom", false, true));
        }

        private static void Overviews(TestRunner runner)
        {
            runner.Section("P3. Overview is not rewritten when Chinese text is already present");

            Func<string, bool> chinese = value => value != null && value == "中文简介";

            runner.Check("empty overview is filled",
                PersonRefreshRules.ShouldApplyOverview("中文简介", "", false, true, chinese));
            runner.Check("non-chinese overview is replaced",
                PersonRefreshRules.ShouldApplyOverview("中文简介", "English bio", false, true, chinese));
            runner.Check("existing chinese overview is kept",
                !PersonRefreshRules.ShouldApplyOverview("另一个中文简介", "中文简介", false, true, chinese));
            runner.Check("same overview is skipped",
                !PersonRefreshRules.ShouldApplyOverview("中文简介", "中文简介", false, true, chinese));
            runner.Check("locked overview",
                !PersonRefreshRules.ShouldApplyOverview("中文简介", "", true, true, chinese));
            runner.Check("non-chinese new overview",
                !PersonRefreshRules.ShouldApplyOverview("English", "", false, false, chinese));
        }

        private static void Dates(TestRunner runner)
        {
            runner.Section("P4. Birth/death dates are filled when absent and differ");

            var birth = new DateTimeOffset(1956, 7, 9, 0, 0, 0, TimeSpan.Zero);

            runner.Check("fills missing birth", PersonRefreshRules.ShouldApplyDate(birth, null));
            runner.Check("keeps same birth", !PersonRefreshRules.ShouldApplyDate(birth, birth));
            runner.Check("changes different birth", PersonRefreshRules.ShouldApplyDate(birth,
                new DateTimeOffset(1950, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            runner.Check("no provider date", !PersonRefreshRules.ShouldApplyDate(null, birth));
        }
    }
}
