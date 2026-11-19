using System;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Emby reports a scheduled task as Completed whenever Execute returns normally, even when
    /// every item failed. Tasks count their per-item outcome (succeeded / failed / skipped) and
    /// use this policy to turn a total failure into a failed task run instead of a silent
    /// "Completed" that nobody notices.
    /// </summary>
    internal static class ScheduledTaskFailurePolicy
    {
        /// <summary>
        /// True when at least one item failed, nothing succeeded and no item was intentionally
        /// skipped. Skips are excluded because restore mode and blacklists can legitimately
        /// process zero items.
        /// </summary>
        internal static bool IsTotalFailure(int succeeded, int failed, int skipped)
        {
            return failed > 0 && succeeded <= 0 && skipped <= 0;
        }

        internal static string Summary(int succeeded, int failed, int skipped)
        {
            return $"succeeded {succeeded}, failed {failed}, skipped {skipped}";
        }

        /// <summary>
        /// Throws so Emby marks the task run as failed. Callers must log the per-item errors and
        /// the summary first; the exception message only carries the counts.
        /// </summary>
        internal static void ThrowIfTotalFailure(string taskName, int succeeded, int failed, int skipped)
        {
            if (!IsTotalFailure(succeeded, failed, skipped)) return;

            throw new InvalidOperationException(
                $"{taskName} - all {failed} attempted item(s) failed; see the errors above");
        }
    }
}
