using System;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Freshness policy for the worker-side media root cache. A shorter TTL means a newly added
    /// library becomes usable within half a minute instead of five, at the cost of re-reading the
    /// virtual folder list once per TTL. Kept separate so the boundary is unit tested.
    /// </summary>
    internal static class MediaRootsCachePolicy
    {
        internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

        internal static bool IsFresh(DateTime cachedAtUtc, DateTime nowUtc)
        {
            return nowUtc - cachedAtUtc < Duration;
        }
    }
}
