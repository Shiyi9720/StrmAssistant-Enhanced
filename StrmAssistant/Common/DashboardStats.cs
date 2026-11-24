using System;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Small, Emby-free pieces of the dashboard calculation so the rounding rules are pinned by
    /// the verification project. The runtime service does the process/filesystem plumbing.
    /// </summary>
    internal static class DashboardStats
    {
        /// <summary>
        /// Memory usage in percent, rounded to one decimal and clamped to [0, 100]. A missing or
        /// non-positive limit reports 0 instead of NaN/Infinity, which would serialize as invalid
        /// JSON for the web client.
        /// </summary>
        public static double UsagePercent(long used, long? limit)
        {
            if (!limit.HasValue || limit.Value <= 0) return 0;

            var value = used * 100.0 / limit.Value;

            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;

            return Math.Round(Math.Max(0, Math.Min(100, value)), 1);
        }

        /// <summary>
        /// Running workers for a semaphore-backed queue. CurrentCount already clamps at zero, so
        /// lowering the limit below the number of holders cannot produce a negative value.
        /// </summary>
        public static int RunningCount(int limit, int currentCount)
        {
            return Math.Max(0, limit - Math.Max(0, currentCount));
        }
    }

    public sealed class DashboardQueueSnapshot
    {
        public int MediaInfoQueued { get; set; }
        public int MediaInfoRunning { get; set; }
        public int FingerprintQueued { get; set; }
        public int FingerprintRunning { get; set; }
        public int IntroQueued { get; set; }
        public int EpisodeRefreshQueued { get; set; }
        public int FavoriteQueued { get; set; }
    }
}
