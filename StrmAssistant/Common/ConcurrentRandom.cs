using System;
using System.Threading;

namespace StrmAssistant.Common
{
    /// <summary>
    /// <see cref="Random"/> for the call sites that run concurrently.
    ///
    /// A single shared <c>Random</c> instance is not thread safe: concurrent <c>Next</c> calls can
    /// corrupt its internal state and start returning zeros. The plugin targets netstandard2.1, so
    /// <c>Random.Shared</c> (.NET 6 and later) is not available; one instance per thread gives the
    /// same "no locking, no shared state" behaviour.
    ///
    /// These call sites only use randomness for retry jitter and user-agent rotation, so an
    /// occasional repeated sequence across two threads is harmless; the seed still mixes in the
    /// managed thread id so that two threads starting in the same tick do not line up exactly.
    /// </summary>
    internal static class ConcurrentRandom
    {
        [ThreadStatic] private static Random _random;

        private static Random Instance =>
            _random ?? (_random = new Random(unchecked(Environment.TickCount * 397 ^
                                                       Thread.CurrentThread.ManagedThreadId)));

        internal static int Next(int maxValue) => Instance.Next(maxValue);

        internal static int Next(int minValue, int maxValue) => Instance.Next(minValue, maxValue);
    }
}
