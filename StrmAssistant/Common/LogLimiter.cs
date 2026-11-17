using System;
using System.Collections.Concurrent;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Limits repeated log lines for failures that can occur once per item or per playback
    /// event. The first failure in each interval is logged with its exception, later ones are
    /// suppressed until the interval elapses again.
    /// </summary>
    internal static class LogLimiter
    {
        private static readonly ConcurrentDictionary<string, long> LastLoggedTicks =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        internal static bool ShouldLog(string key, TimeSpan interval)
        {
            if (string.IsNullOrEmpty(key)) return true;

            var now = DateTime.UtcNow.Ticks;

            while (true)
            {
                if (!LastLoggedTicks.TryGetValue(key, out var last))
                {
                    if (LastLoggedTicks.TryAdd(key, now)) return true;
                    continue;
                }

                if (now - last < interval.Ticks) return false;

                if (LastLoggedTicks.TryUpdate(key, now, last)) return true;
            }
        }

        internal static void Reset()
        {
            LastLoggedTicks.Clear();
        }
    }
}
