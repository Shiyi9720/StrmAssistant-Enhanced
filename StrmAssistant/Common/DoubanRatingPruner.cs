using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Bounds the Douban rating table. Ratings are cheap to keep but expensive to re-fetch, so
    /// the limits are deliberately generous: entries older than <see cref="MaxAge"/> are dropped
    /// and, if the table still exceeds <see cref="MaxEntries"/>, the least recently updated ones
    /// are removed. Kept free of Emby types so the pruning rules can be unit tested.
    /// </summary>
    internal static class DoubanRatingPruner
    {
        internal const int MaxEntries = 50000;

        internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(180);

        /// <summary>
        /// Removes stale and excess entries in place and returns how many were removed.
        /// Recent entries are never dropped unless the hard size cap forces it, in which case
        /// the oldest entries go first.
        /// </summary>
        internal static int Prune<T>(IDictionary<string, T> entries, Func<T, DateTime> updatedAtUtc,
            DateTime nowUtc) where T : class
        {
            if (entries is null || entries.Count == 0) return 0;

            var cutoff = nowUtc - MaxAge;
            var removed = 0;

            var stale = entries
                .Where(pair => pair.Value is null || updatedAtUtc(pair.Value) < cutoff)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in stale)
            {
                entries.Remove(key);
                removed++;
            }

            if (entries.Count <= MaxEntries) return removed;

            var excess = entries.Count - MaxEntries;

            var oldest = entries
                .OrderBy(pair => updatedAtUtc(pair.Value))
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(excess)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in oldest)
            {
                entries.Remove(key);
                removed++;
            }

            return removed;
        }
    }
}