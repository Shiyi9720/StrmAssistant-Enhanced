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
            if (sourceRuntime <= 0 || targetRuntime <= 0) return positionTicks;

            var converted = (long)Math.Round(positionTicks * (double)targetRuntime / sourceRuntime,
                MidpointRounding.AwayFromZero);

            if (converted < 0) return 0;

            return Math.Min(converted, targetRuntime);
        }
    }
}
