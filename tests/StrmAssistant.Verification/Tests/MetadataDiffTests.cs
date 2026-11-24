using StrmAssistant.Common;
using System;
using System.Collections.Generic;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// The metadata.update notification reports a "字段: 旧 → 新" diff. These tests pin the
    /// diff rules through the dictionary-based entry point (the production path reads the same
    /// twenty fields off a BaseItem): a null before-image means "no comparison yet" and must
    /// yield no lines, and each changed field is rendered in field order.
    /// </summary>
    internal static class MetadataDiffTests
    {
        public static void Run(TestRunner runner)
        {
            NullBeforeYieldsNoLines(runner);
            UnchangedYieldsNoLines(runner);
            ChangedFieldsAreReported(runner);
            EmptyVersusMissingAreTheSame(runner);
            FieldOrderFollowsDeclaration(runner);
        }

        private static void NullBeforeYieldsNoLines(TestRunner runner)
        {
            runner.Section("S1. No before-image (first observation) yields no lines");

            var after = new Dictionary<string, string> { ["Name"] = "New" };

            runner.Check("null before yields no lines",
                MetadataDiff.Compare(null, after).Count == 0);
            runner.Check("null after yields no lines",
                MetadataDiff.Compare(after, null).Count == 0);
            runner.Check("both null yields no lines",
                MetadataDiff.Compare(null, null).Count == 0);
        }

        private static void UnchangedYieldsNoLines(TestRunner runner)
        {
            runner.Section("S2. Identical snapshots yield no lines");

            var before = new Dictionary<string, string>
            {
                ["Name"] = "Same", ["ProductionYear"] = "2024"
            };
            var after = new Dictionary<string, string>
            {
                ["Name"] = "Same", ["ProductionYear"] = "2024"
            };

            runner.Check("no changed field",
                MetadataDiff.Compare(before, after).Count == 0);
        }

        private static void ChangedFieldsAreReported(TestRunner runner)
        {
            runner.Section("S3. A changed field renders 旧 → 新");

            var before = new Dictionary<string, string> { ["Name"] = "Old" };
            var after = new Dictionary<string, string> { ["Name"] = "New" };

            var changes = MetadataDiff.Compare(before, after);

            runner.Check("one line", changes.Count == 1);
            runner.EqualString("line carries label and both values",
                "名称: Old → New", changes.Count == 1 ? changes[0] : "");
        }

        private static void EmptyVersusMissingAreTheSame(TestRunner runner)
        {
            runner.Section("S4. Empty string and missing key both mean 空值");

            var before = new Dictionary<string, string> { ["Tagline"] = "" };
            var after = new Dictionary<string, string>();

            var changes = MetadataDiff.Compare(before, after);

            runner.Check("empty vs missing is no change", changes.Count == 0);

            var beforeValue = new Dictionary<string, string> { ["Tagline"] = "was here" };
            var afterEmpty = new Dictionary<string, string> { ["Tagline"] = "" };

            var cleared = MetadataDiff.Compare(beforeValue, afterEmpty);

            runner.Check("cleared to 空值", cleared.Count == 1 &&
                cleared[0].EndsWith("→ （空）", StringComparison.Ordinal));
        }

        private static void FieldOrderFollowsDeclaration(TestRunner runner)
        {
            runner.Section("S5. Multiple changes come out in field order (Name before Year)");

            var before = new Dictionary<string, string>
            {
                ["ProductionYear"] = "2023", ["Name"] = "Old", ["SortName"] = "sort"
            };
            var after = new Dictionary<string, string>
            {
                ["ProductionYear"] = "2024", ["Name"] = "New", ["SortName"] = "sort"
            };

            var changes = MetadataDiff.Compare(before, after);

            runner.Check("two lines", changes.Count == 2);
            runner.Check("Name (declared first) comes first",
                changes.Count == 2 && changes[0].StartsWith("名称:", StringComparison.Ordinal));
            runner.Check("Year comes second",
                changes.Count == 2 && changes[1].StartsWith("年份:", StringComparison.Ordinal));
        }
    }
}
