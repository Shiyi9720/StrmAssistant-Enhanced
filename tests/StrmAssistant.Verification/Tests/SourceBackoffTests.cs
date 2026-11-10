using StrmAssistant.Common;
using System;

namespace StrmAssistant.Verification
{
    internal static class SourceBackoffTests
    {
        private const double ToleranceSeconds = 30;

        public static void Run(TestRunner runner)
        {
            NewKeyIsClean(runner);
            FailureLadder(runner);
            SuccessResetsLadder(runner);
            EmptyAndNullKeys(runner);
            KeysAreIndependent(runner);
        }

        private static void NewKeyIsClean(TestRunner runner)
        {
            runner.Section("A1. new key");

            var key = NewKey("new");

            runner.Check("A1 ShouldSkip is false", !SourceBackoff.ShouldSkip(key));
            runner.Check("A1 UnavailableUntil is null", SourceBackoff.UnavailableUntil(key) is null,
                "actual " + Format(SourceBackoff.UnavailableUntil(key)));
        }

        private static void FailureLadder(TestRunner runner)
        {
            runner.Section("A2/A3. failure ladder (10m, 30m, 2h, 6h, 6h)");

            var key = NewKey("ladder");
            var expectedMinutes = new[] { 10d, 30d, 120d, 360d, 360d };

            for (var i = 0; i < expectedMinutes.Length; i++)
            {
                SourceBackoff.ReportFailure(key);

                CheckWindow(runner, "A2 failure " + (i + 1) + " window",
                    SourceBackoff.UnavailableUntil(key), TimeSpan.FromMinutes(expectedMinutes[i]));

                runner.Check("A3 failure " + (i + 1) + " ShouldSkip is true",
                    SourceBackoff.ShouldSkip(key));
            }
        }

        private static void SuccessResetsLadder(TestRunner runner)
        {
            runner.Section("A4. success clears the state and the next failure restarts at 10m");

            var key = NewKey("recover");

            SourceBackoff.ReportFailure(key);
            SourceBackoff.ReportFailure(key);
            SourceBackoff.ReportSuccess(key);

            runner.Check("A4 ShouldSkip is false after ReportSuccess",
                !SourceBackoff.ShouldSkip(key));
            runner.Check("A4 UnavailableUntil is null after ReportSuccess",
                SourceBackoff.UnavailableUntil(key) is null);

            SourceBackoff.ReportFailure(key);
            CheckWindow(runner, "A4 next failure restarts at 10m",
                SourceBackoff.UnavailableUntil(key), TimeSpan.FromMinutes(10));
        }

        private static void EmptyAndNullKeys(TestRunner runner)
        {
            runner.Section("A5. null/empty keys are inert");

            var sentinel = NewKey("sentinel");
            SourceBackoff.ReportFailure(sentinel);

            var threw = false;

            try
            {
                SourceBackoff.ReportFailure(null);
                SourceBackoff.ReportFailure(string.Empty);
                SourceBackoff.ReportSuccess(null);
                SourceBackoff.ReportSuccess(string.Empty);
            }
            catch (Exception ex)
            {
                threw = true;
                runner.Check("A5 null/empty calls do not throw", false,
                    ex.GetType().Name + ": " + ex.Message);
            }

            if (!threw) runner.Check("A5 null/empty calls do not throw", true);

            runner.Check("A5 null key is not skipped", !SourceBackoff.ShouldSkip(null));
            runner.Check("A5 empty key is not skipped", !SourceBackoff.ShouldSkip(string.Empty));
            runner.Check("A5 null key has no window", SourceBackoff.UnavailableUntil(null) is null);
            runner.Check("A5 empty key has no window", SourceBackoff.UnavailableUntil(string.Empty) is null);

            CheckWindow(runner, "A5 unrelated key keeps its state",
                SourceBackoff.UnavailableUntil(sentinel), TimeSpan.FromMinutes(10));
        }

        private static void KeysAreIndependent(TestRunner runner)
        {
            runner.Section("A6. different keys do not interfere");

            var first = NewKey("independent-a");
            var second = NewKey("independent-b");

            SourceBackoff.ReportFailure(first);
            SourceBackoff.ReportFailure(second);
            SourceBackoff.ReportFailure(second);

            CheckWindow(runner, "A6 first key stays at tier 1",
                SourceBackoff.UnavailableUntil(first), TimeSpan.FromMinutes(10));
            CheckWindow(runner, "A6 second key reaches tier 2",
                SourceBackoff.UnavailableUntil(second), TimeSpan.FromMinutes(30));

            SourceBackoff.ReportSuccess(first);

            runner.Check("A6 clearing first key leaves second key skipped",
                !SourceBackoff.ShouldSkip(first) && SourceBackoff.ShouldSkip(second));
            CheckWindow(runner, "A6 second key window unchanged after clearing first",
                SourceBackoff.UnavailableUntil(second), TimeSpan.FromMinutes(30));
        }

        private static void CheckWindow(TestRunner runner, string name, DateTime? until,
            TimeSpan expected)
        {
            if (!until.HasValue)
            {
                runner.Check(name, false, "UnavailableUntil was null");
                return;
            }

            var remaining = (until.Value - DateTime.UtcNow).TotalSeconds;
            var ok = remaining >= expected.TotalSeconds - ToleranceSeconds &&
                     remaining <= expected.TotalSeconds + ToleranceSeconds;

            runner.Check(name, ok,
                "expected ~" + expected.TotalSeconds.ToString("F0") + "s, actual " +
                remaining.ToString("F1") + "s");
        }

        private static string NewKey(string area)
        {
            return "verify-" + area + "-" + Guid.NewGuid().ToString("N");
        }

        private static string Format(DateTime? value)
        {
            return value.HasValue ? value.Value.ToString("O") : "<null>";
        }
    }
}
