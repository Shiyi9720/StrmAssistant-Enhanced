using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Tracks per-source failure streaks so that a rate limited or unreachable third party
    /// service is left alone for progressively longer periods instead of being hammered again
    /// as soon as a fixed pause expires. A single success clears the streak.
    /// </summary>
    internal static class SourceBackoff
    {
        private sealed class State
        {
            public int ConsecutiveFailures;
            public int Tier = -1;
            public DateTime UntilUtc = DateTime.MinValue;
        }

        private static readonly TimeSpan[] Ladder =
        {
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(2),
            TimeSpan.FromHours(6)
        };

        private static readonly ConcurrentDictionary<string, State> States =
            new ConcurrentDictionary<string, State>(StringComparer.Ordinal);

        private static ILogger Logger => Plugin.Instance?.Logger;

        internal static bool ShouldSkip(string key)
        {
            return UnavailableUntil(key).HasValue;
        }

        internal static DateTime? UnavailableUntil(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (!States.TryGetValue(key, out var state)) return null;

            lock (state)
            {
                var until = state.UntilUtc;
                return DateTime.UtcNow < until ? until : (DateTime?) null;
            }
        }

        internal static void ReportFailure(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            var state = States.GetOrAdd(key, _ => new State());
            int failures;
            int tier;
            DateTime until;

            lock (state)
            {
                state.ConsecutiveFailures++;
                failures = state.ConsecutiveFailures;
                tier = Math.Min(failures - 1, Ladder.Length - 1);
                until = DateTime.UtcNow.Add(Ladder[tier]);
                state.UntilUtc = until;

                if (tier == state.Tier) return;
                state.Tier = tier;
            }

            Logger?.Info("SourceBackoff - " + key + " failed " + failures + " time(s) in a row, pausing until " +
                         until.ToString("yyyy-MM-dd HH:mm") + " UTC");
        }

        internal static void ReportSuccess(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!States.TryRemove(key, out _)) return;

            Logger?.Debug("SourceBackoff - " + key + " recovered, backoff cleared");
        }
    }
}
