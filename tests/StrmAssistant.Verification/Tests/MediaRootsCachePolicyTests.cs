using System;
using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// The worker-side media root cache must refresh quickly enough that a newly added library
    /// becomes usable without a restart. These assertions pin the 30 second bound.
    /// </summary>
    internal static class MediaRootsCachePolicyTests
    {
        public static void Run(TestRunner runner)
        {
            runner.Section("media roots cache - 30 second freshness window");

            var now = DateTime.UtcNow;

            runner.Check("freshly cached roots are reused",
                MediaRootsCachePolicy.IsFresh(now, now));
            runner.Check("roots cached 29s ago are reused",
                MediaRootsCachePolicy.IsFresh(now.AddSeconds(-29), now));
            runner.Check("roots cached 30s ago are stale",
                !MediaRootsCachePolicy.IsFresh(now.AddSeconds(-30), now));
            runner.Check("roots cached 5 minutes ago are stale",
                !MediaRootsCachePolicy.IsFresh(now.AddMinutes(-5), now));

            runner.Check("max stale window is at most 30 seconds",
                MediaRootsCachePolicy.Duration <= TimeSpan.FromSeconds(30),
                "duration=" + MediaRootsCachePolicy.Duration);
        }
    }
}
