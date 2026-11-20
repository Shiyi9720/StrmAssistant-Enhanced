using System;
using System.Threading;
using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// A scheduled task that fails on every attempted item must not be reported as Completed.
    /// These cases pin the rule: a skip does not mask failures, while an all-skip run and a
    /// partial failure stay normal.
    /// </summary>
    internal static class ScheduledTaskFailurePolicyTests
    {
        public static void Run(TestRunner runner)
        {
            runner.Section("scheduled task outcome - failed run");

            runner.Check("nothing attempted is not a failure",
                !ScheduledTaskFailurePolicy.IsFailedRun(0, 0, 0));
            runner.Check("one failure and no success is a failed run",
                ScheduledTaskFailurePolicy.IsFailedRun(0, 1, 0));
            runner.Check("three failures and no success is a failed run",
                ScheduledTaskFailurePolicy.IsFailedRun(0, 3, 0));
            runner.Check("negative success count still means nothing succeeded",
                ScheduledTaskFailurePolicy.IsFailedRun(-1, 2, 0));

            runner.Section("scheduled task outcome - skips do not mask failures");

            runner.Check("one skip does not rescue 100 failures",
                ScheduledTaskFailurePolicy.IsFailedRun(0, 100, 1));
            runner.Check("many skips do not rescue one failure",
                ScheduledTaskFailurePolicy.IsFailedRun(0, 1, 100));
            runner.Check("a success still makes a skip-plus-failure run partial",
                !ScheduledTaskFailurePolicy.IsFailedRun(1, 100, 1));

            runner.Section("scheduled task outcome - normal runs");

            runner.Check("a skip is not a failure", !ScheduledTaskFailurePolicy.IsFailedRun(0, 0, 1));
            runner.Check("only skips is not a failure", !ScheduledTaskFailurePolicy.IsFailedRun(0, 0, 5));
            runner.Check("one success is not a failed run",
                !ScheduledTaskFailurePolicy.IsFailedRun(1, 3, 0));
            runner.Check("successes with skips and failures stay partial",
                !ScheduledTaskFailurePolicy.IsFailedRun(10, 3, 7));

            runner.Section("scheduled task outcome - throw behaviour");

            var thrown = false;

            try
            {
                ScheduledTaskFailurePolicy.ThrowIfFailed("Task", 0, 2, 0);
            }
            catch (InvalidOperationException)
            {
                thrown = true;
            }

            runner.Check("a failed run throws so Emby marks the task failed", thrown);

            var skippedFailureThrown = false;

            try
            {
                ScheduledTaskFailurePolicy.ThrowIfFailed("Task", 0, 2, 1);
            }
            catch (InvalidOperationException)
            {
                skippedFailureThrown = true;
            }

            runner.Check("a skip does not suppress the throw", skippedFailureThrown);

            var noThrow = true;

            try
            {
                ScheduledTaskFailurePolicy.ThrowIfFailed("Task", 0, 0, 0);
                ScheduledTaskFailurePolicy.ThrowIfFailed("Task", 1, 2, 0);
                ScheduledTaskFailurePolicy.ThrowIfFailed("Task", 0, 0, 5);
                ScheduledTaskFailurePolicy.ThrowIfFailed("Task", 1, 2, 3);
            }
            catch (Exception)
            {
                noThrow = false;
            }

            runner.Check("empty, partial and all-skipped runs do not throw", noThrow);

            runner.Section("scheduled task outcome - item cancellation vs task cancellation");

            using var notCancelled = new CancellationTokenSource();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            runner.Check("a clear task token means the item timed out",
                !ScheduledTaskFailurePolicy.IsTaskCancellation(notCancelled.Token));
            runner.Check("a set task token means the whole task was cancelled",
                ScheduledTaskFailurePolicy.IsTaskCancellation(cancelled.Token));

            runner.Section("scheduled task outcome - summary");

            runner.EqualString("summary contains all three counters",
                "succeeded 7, failed 2, skipped 1",
                ScheduledTaskFailurePolicy.Summary(7, 2, 1));
        }
    }
}
