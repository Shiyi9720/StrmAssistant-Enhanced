using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// The dashboard must never emit NaN/Infinity or a negative byte count: the web client
    /// renders the values directly. These pin the clamp/round rules.
    /// </summary>
    internal static class DashboardStatsTests
    {
        public static void Run(TestRunner runner)
        {
            UsagePercent(runner);
            UsagePercentEdge(runner);
            RunningCount(runner);
        }

        private static void UsagePercent(TestRunner runner)
        {
            runner.Section("D1. Usage percent rounds to one decimal");

            runner.Check("half", DashboardStats.UsagePercent(50, 100) == 50);
            runner.Check("one third", DashboardStats.UsagePercent(1, 3) == 33.3);
            runner.Check("round up", DashboardStats.UsagePercent(2, 3) == 66.7);
        }

        private static void UsagePercentEdge(TestRunner runner)
        {
            runner.Section("D2. Usage percent clamps and handles a missing limit");

            runner.Check("over limit clamps to 100", DashboardStats.UsagePercent(150, 100) == 100);
            runner.Check("negative use clamps to 0", DashboardStats.UsagePercent(-5, 100) == 0);
            runner.Check("null limit is 0", DashboardStats.UsagePercent(50, null) == 0);
            runner.Check("zero limit is 0", DashboardStats.UsagePercent(50, 0) == 0);
            runner.Check("negative limit is 0", DashboardStats.UsagePercent(50, -1) == 0);
        }

        private static void RunningCount(TestRunner runner)
        {
            runner.Section("D3. Queue running count never goes negative");

            runner.Check("idle", DashboardStats.RunningCount(4, 4) == 0);
            runner.Check("two running", DashboardStats.RunningCount(4, 2) == 2);
            runner.Check("limit lowered below holders", DashboardStats.RunningCount(1, 0) == 1);
            runner.Check("bad current clamps", DashboardStats.RunningCount(4, -2) == 4);
        }
    }
}
