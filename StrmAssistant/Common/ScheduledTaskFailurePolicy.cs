using System;
using System.Threading;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Emby reports a scheduled task as Completed whenever Execute returns normally, even when
    /// every attempted item failed. Tasks count their per-item outcome (succeeded / failed /
    /// skipped) and use this policy to turn an all-attempts-failed run into a failed task run
    /// instead of a silent "Completed" that nobody notices.
    /// </summary>
    internal static class ScheduledTaskFailurePolicy
    {
        /// <summary>
        /// True when at least one item failed and nothing succeeded. Skips deliberately do not
        /// rescue the run: a skip means "not processed on purpose", not "processed
        /// successfully", so 100 failures plus 1 skip is still a failure. A run that only
        /// skipped (no failure, no success) stays normal, because restore mode, blacklists and
        /// out-of-scope items can legitimately process zero items. A partial failure stays
        /// normal as well: Emby only has Completed / Failed, and one bad item out of thousands
        /// would otherwise hide every other result.
        /// </summary>
        internal static bool IsFailedRun(int succeeded, int failed, int skipped)
        {
            return failed > 0 && succeeded <= 0;
        }

        /// <summary>
        /// A per-item <see cref="OperationCanceledException"/> means the whole task was
        /// cancelled only when the task's own token is set. Otherwise the item's own timeout (or
        /// a linked per-item token) fired: the item did not complete, so callers must count it
        /// as failed instead of letting it fall through as a success.
        /// </summary>
        internal static bool IsTaskCancellation(CancellationToken cancellationToken)
        {
            return cancellationToken.IsCancellationRequested;
        }

        internal static string Summary(int succeeded, int failed, int skipped)
        {
            return $"succeeded {succeeded}, failed {failed}, skipped {skipped}";
        }

        /// <summary>
        /// Throws so Emby marks the task run as failed. Callers must log the per-item errors and
        /// the summary first; the exception message only carries the counts.
        /// </summary>
        internal static void ThrowIfFailed(string taskName, int succeeded, int failed, int skipped)
        {
            if (!IsFailedRun(succeeded, failed, skipped)) return;

            throw new InvalidOperationException(
                $"{taskName} - {failed} item(s) failed and none succeeded; see the errors above");
        }
    }
}
