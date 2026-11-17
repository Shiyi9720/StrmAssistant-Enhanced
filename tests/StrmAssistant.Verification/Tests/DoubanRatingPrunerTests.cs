using System;
using System.Collections.Generic;
using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Regression coverage for the bounded Douban rating table: stale entries are dropped, recent
    /// entries survive, and the hard size cap removes the least recently updated rows first.
    /// </summary>
    internal static class DoubanRatingPrunerTests
    {
        public static void Run(TestRunner runner)
        {
            UnderLimitsIsNoOp(runner);
            RecentEntriesSurvive(runner);
            StaleEntriesAreDropped(runner);
            SizeCapKeepsNewest(runner);
            EmptyTableIsNoOp(runner);
        }

        private sealed class Entry
        {
            public DateTime UpdatedAtUtc { get; set; }
        }

        private static void UnderLimitsIsNoOp(TestRunner runner)
        {
            runner.Section("douban pruner - recent table under the cap is untouched");

            var now = DateTime.UtcNow;
            var entries = new Dictionary<string, Entry>
            {
                ["1"] = new Entry {UpdatedAtUtc = now.AddDays(-1)},
                ["2"] = new Entry {UpdatedAtUtc = now.AddDays(-90)}
            };

            var removed = DoubanRatingPruner.Prune(entries, e => e.UpdatedAtUtc, now);

            runner.EqualLong("nothing removed under the limits", 0, removed);
            runner.EqualLong("both recent entries kept", 2, entries.Count);
        }

        private static void RecentEntriesSurvive(TestRunner runner)
        {
            runner.Section("douban pruner - entries inside the age window are kept");

            var now = DateTime.UtcNow;
            var entries = new Dictionary<string, Entry>
            {
                ["oldest-allowed"] = new Entry
                {
                    UpdatedAtUtc = now - DoubanRatingPruner.MaxAge + TimeSpan.FromMinutes(1)
                },
                ["brand-new"] = new Entry {UpdatedAtUtc = now}
            };

            DoubanRatingPruner.Prune(entries, e => e.UpdatedAtUtc, now);

            runner.Check("entry just inside the age window kept",
                entries.ContainsKey("oldest-allowed"));
            runner.Check("fresh entry kept", entries.ContainsKey("brand-new"));
        }

        private static void StaleEntriesAreDropped(TestRunner runner)
        {
            runner.Section("douban pruner - entries past the age window are dropped");

            var now = DateTime.UtcNow;
            var entries = new Dictionary<string, Entry>
            {
                ["stale"] = new Entry
                {
                    UpdatedAtUtc = now - DoubanRatingPruner.MaxAge - TimeSpan.FromMinutes(1)
                },
                ["recent"] = new Entry {UpdatedAtUtc = now.AddDays(-30)}
            };

            var removed = DoubanRatingPruner.Prune(entries, e => e.UpdatedAtUtc, now);

            runner.EqualLong("one stale entry removed", 1, removed);
            runner.Check("stale entry gone", !entries.ContainsKey("stale"));
            runner.Check("recent entry kept", entries.ContainsKey("recent"));
        }

        private static void SizeCapKeepsNewest(TestRunner runner)
        {
            runner.Section("douban pruner - size cap drops the oldest entries first");

            var now = DateTime.UtcNow;
            var entries = new Dictionary<string, Entry>(DoubanRatingPruner.MaxEntries + 25);
            var baseTime = now - TimeSpan.FromDays(100);

            for (var i = 0; i < DoubanRatingPruner.MaxEntries + 25; i++)
            {
                entries["key-" + i.ToString("D6")] = new Entry
                {
                    UpdatedAtUtc = baseTime.AddSeconds(i)
                };
            }

            var removed = DoubanRatingPruner.Prune(entries, e => e.UpdatedAtUtc, now);

            runner.EqualLong("excess entries removed", 25, removed);
            runner.EqualLong("table capped at the maximum", DoubanRatingPruner.MaxEntries,
                entries.Count);
            runner.Check("oldest entry dropped", !entries.ContainsKey("key-000000"));
            runner.Check("oldest surviving entry kept", entries.ContainsKey("key-000025"));
            runner.Check("newest entry kept",
                entries.ContainsKey("key-" + (DoubanRatingPruner.MaxEntries + 24).ToString("D6")));
        }

        private static void EmptyTableIsNoOp(TestRunner runner)
        {
            runner.Section("douban pruner - empty table");

            runner.EqualLong("empty table removes nothing", 0,
                DoubanRatingPruner.Prune(new Dictionary<string, Entry>(), e => e.UpdatedAtUtc,
                    DateTime.UtcNow));
        }
    }
}
