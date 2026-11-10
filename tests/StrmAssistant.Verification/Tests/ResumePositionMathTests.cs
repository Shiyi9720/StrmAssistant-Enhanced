using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    internal static class ResumePositionMathTests
    {
        // Emby ticks are 100ns, so one minute is 600,000,000 ticks.
        private const long TicksPerMinute = 600_000_000L;

        public static void Run(TestRunner runner)
        {
            Ratio(runner);
            Clamp(runner);
            ZeroRuntime(runner);
            NonPositivePosition(runner);
            AwayFromZeroRounding(runner);
        }

        private static void Ratio(TestRunner runner)
        {
            runner.Section("C1. proportional conversion");

            // The task sheet gives 800,000,000 for a 10-minute point with 60/80-minute
            // runtimes. In Emby's 100ns ticks 10 minutes is 6,000,000,000, which converts to
            // 8,000,000,000; the sheet's 600,000,000 input is one minute. Both are asserted so
            // the stated number and the real tick unit are covered.
            runner.EqualLong("C1 task-sheet numbers 600,000,000 * 80/60", 800_000_000L,
                ResumePositionMath.ConvertPosition(600_000_000L, 60 * TicksPerMinute,
                    80 * TicksPerMinute));

            runner.EqualLong("C1 real Emby ticks 10min with 60/80min runtimes",
                10 * TicksPerMinute * 80 / 60,
                ResumePositionMath.ConvertPosition(10 * TicksPerMinute, 60 * TicksPerMinute,
                    80 * TicksPerMinute));
        }

        private static void Clamp(TestRunner runner)
        {
            runner.Section("C2. result never exceeds target runtime");

            const long sourceRuntime = 60 * TicksPerMinute;
            const long targetRuntime = 80 * TicksPerMinute;

            // 100 minutes into a 60-minute source is out of range; the result must be clamped.
            var converted = ResumePositionMath.ConvertPosition(100 * TicksPerMinute,
                sourceRuntime, targetRuntime);

            runner.EqualLong("C2 out-of-range input clamps to target runtime", targetRuntime,
                converted);
            runner.Check("C2 converted <= target runtime", converted <= targetRuntime,
                "converted " + converted + ", target " + targetRuntime);
        }

        private static void ZeroRuntime(TestRunner runner)
        {
            runner.Section("C3. zero runtime returns the position unchanged");

            runner.EqualLong("C3 source runtime 0", 12345L,
                ResumePositionMath.ConvertPosition(12345L, 0L, 100L));
            runner.EqualLong("C3 target runtime 0", 12345L,
                ResumePositionMath.ConvertPosition(12345L, 100L, 0L));
            runner.EqualLong("C3 both runtimes 0", 12345L,
                ResumePositionMath.ConvertPosition(12345L, 0L, 0L));
        }

        private static void NonPositivePosition(TestRunner runner)
        {
            runner.Section("C4. non-positive position returns 0");

            runner.EqualLong("C4 position 0", 0L,
                ResumePositionMath.ConvertPosition(0L, 60 * TicksPerMinute, 80 * TicksPerMinute));
            runner.EqualLong("C4 negative position", 0L,
                ResumePositionMath.ConvertPosition(-1L, 60 * TicksPerMinute, 80 * TicksPerMinute));
            runner.EqualLong("C4 large negative position", 0L,
                ResumePositionMath.ConvertPosition(-999_999_999L, 60 * TicksPerMinute,
                    80 * TicksPerMinute));
        }

        private static void AwayFromZeroRounding(TestRunner runner)
        {
            runner.Section("C5. MidpointRounding.AwayFromZero");

            // 1 * 10 / 4 = 2.5 exactly. AwayFromZero rounds to 3; banker's rounding (ToEven)
            // would produce 2, so this case pins the expected mode down.
            runner.EqualLong("C5 2.5 rounds away from zero to 3", 3L,
                ResumePositionMath.ConvertPosition(1L, 4L, 10L));

            // 13 * 10 / 20 = 6.5 exactly. AwayFromZero rounds to 7; ToEven would round to 6.
            runner.EqualLong("C5 6.5 rounds away from zero to 7", 7L,
                ResumePositionMath.ConvertPosition(13L, 20L, 10L));
        }
    }
}
