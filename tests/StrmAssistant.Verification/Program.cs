using System;
using System.Globalization;

namespace StrmAssistant.Verification
{
    internal static class Program
    {
        private static int Main()
        {
            var runner = new TestRunner();

            Console.WriteLine("StrmAssistant verification");
            Console.WriteLine("Started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Console.WriteLine("Runtime: " + Environment.Version);
            Console.WriteLine("BaseDirectory: " + AppContext.BaseDirectory);

            RunGroup(runner, "ResumePositionMath", ResumePositionMathTests.Run);
            RunGroup(runner, "SourceBackoff", SourceBackoffTests.Run);
            RunGroup(runner, "DvP7Api", DvP7Tests.Run);
            RunGroup(runner, "SourceBackoffHttp", SourceBackoffHttpTests.Run);
            RunGroup(runner, "DvP7DetectAsync", DvP7DetectTests.Run);
            RunGroup(runner, "ProbeExitCode", ProbeExitCodeTests.Run);
            RunGroup(runner, "ResumeSequence", ResumeSequenceTests.Run);

            return runner.Finish();
        }

        private static void RunGroup(TestRunner runner, string name, Action<TestRunner> group)
        {
            try
            {
                group(runner);
            }
            catch (Exception ex)
            {
                runner.Check(name + " group crashed", false, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    internal sealed class TestRunner
    {
        private int _pass;
        private int _fail;
        private int _skip;

        public void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("== " + name + " ==");
        }

        public void Check(string name, bool ok, string detail = null)
        {
            if (ok)
            {
                _pass++;
                Console.WriteLine("[PASS] " + name);
            }
            else
            {
                _fail++;
                Console.WriteLine("[FAIL] " + name + (string.IsNullOrEmpty(detail) ? "" : " - " + detail));
            }
        }

        public void Skip(string name, string reason)
        {
            _skip++;
            Console.WriteLine("[SKIP] " + name + " - " + reason);
        }

        public void EqualLong(string name, long expected, long actual)
        {
            Check(name, expected == actual, "expected " + expected + ", actual " + actual);
        }

        public void EqualString(string name, string expected, string actual)
        {
            Check(name, string.Equals(expected, actual, StringComparison.Ordinal),
                "expected '" + (expected ?? "<null>") + "', actual '" + (actual ?? "<null>") + "'");
        }

        public int Finish()
        {
            Console.WriteLine();
            Console.WriteLine("SUMMARY: " + _pass + " passed, " + _fail + " failed, " + _skip +
                              " skipped");

            return _fail == 0 ? 0 : 1;
        }
    }
}
