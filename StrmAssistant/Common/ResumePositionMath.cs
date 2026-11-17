using System;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Pure math for translating a resume point between versions with different runtimes.
    /// Kept in its own file so the verification project can compile it without the Emby and
    /// plugin dependencies that <see cref="VersionPlaybackApi"/> pulls in.
    /// </summary>
    internal static class ResumePositionMath
    {
        internal static long ConvertPosition(long positionTicks, long sourceRuntime, long targetRuntime)
        {
            if (positionTicks <= 0) return 0;
            if (targetRuntime <= 0) return positionTicks;

            // The source runtime is unknown, so no proportional conversion is possible - but the
            // position must still be clamped to the target version, otherwise a long cut's resume
            // point lands past the end of a shorter version and playback restarts from zero.
            if (sourceRuntime <= 0) return Math.Min(positionTicks, targetRuntime);

            var converted = (long)Math.Round(positionTicks * (double)targetRuntime / sourceRuntime,
                MidpointRounding.AwayFromZero);

            if (converted < 0) return 0;

            return Math.Min(converted, targetRuntime);
        }
    }
}
