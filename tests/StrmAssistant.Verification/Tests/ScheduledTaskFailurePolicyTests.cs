using System;
using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// A scheduled task that fails on every item must not be reported as Completed. These cases
    /// pin the rule so a partial or empty run stays a normal success.
    /// </summary>
    internal static class ScheduledTaskFailurePolicyTests
    {
        public static void Run(TestRunner runner)
        {
            runner.Section("scheduled task outcome - total failure");

            runner.Check("nothing attempted is not a failure",
                !ScheduledTaskFailurePolicy.IsTotalFailure(0, 0, 0));
            runner.Check("one failure and no success is a total failure",
                ScheduledTaskFailurePolicy.IsTotalFailure(0, 1, 0));
            runner.Check("three failures and no success is a total failure",
                ScheduledTaskFailurePolicy.IsTotalFailure(0, 3, 0));
            runner.Check("one success is not a total failure",
                !ScheduledTaskFailurePolicy.IsTotalFailure(1, 3, 0));
            runner.Check("a skip is not a total failure",
                !ScheduledTaskFailurePolicy.IsTotalFailure(0, 3, 1));
            runner.Check("only skips is not a failure",
                !ScheduledTaskFailurePolicy.IsTotalFailure(0, 0, 5));
            runner.Check("negative success count still means nothing succeeded",
                ScheduledTaskFailurePolicy.IsTotalFailure(-1, 2, 0));

            runner.Section("scheduled task outcome - throw behaviour");

            var thrown = false;

            try
            {
                ScheduledTaskFailurePolicy.ThrowIfTotalFailure("Task", 0, 2, 0);
            }
            catch (InvalidOperationException)
            {
                thrown = true;
            }

            runner.Check("total failure throws so Emby marks the task failed", thrown);

            var noThrow = true;

            try
            {
                ScheduledTaskFailurePolicy.ThrowIfTotalFailure("Task", 0, 0, 0);
                ScheduledTaskFailurePolicy.ThrowIfTotalFailure("Task", 1, 2, 0);
                ScheduledTaskFailurePolicy.ThrowIfTotalFailure("Task", 0, 2, 1);
            }
            catch (Exception)
            {
                noThrow = false;
            }

            runner.Check("partial, empty and skipped runs do not throw", noThrow);

            runner.Section("scheduled task outcome - summary");

            runner.EqualString("summary contains all three counters",
                "succeeded 7, failed 2, skipped 1",
                ScheduledTaskFailurePolicy.Summary(7, 2, 1));
        }
    }
}
