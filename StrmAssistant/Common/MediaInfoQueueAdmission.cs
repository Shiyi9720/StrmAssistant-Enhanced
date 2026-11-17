using System;
using System.Collections.Concurrent;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Admission and pending-marker rules for the media info queue. Kept free of Emby types so
    /// the 1.0.11 dedup invariants (register on enqueue, keep while deferred, clear only when the
    /// item is processed or dropped) can be covered by the verification project without shimming
    /// the whole queue worker.
    /// </summary>
    internal static class MediaInfoQueueAdmission
    {
        /// <summary>
        /// Registers <paramref name="itemId"/> and runs <paramref name="enqueue"/>. Returns false
        /// when the id is already pending, in which case nothing is enqueued. If enqueueing
        /// throws, the marker is rolled back so the item can be retried.
        /// </summary>
        internal static bool TryEnqueue(ConcurrentDictionary<long, byte> pending, long itemId,
            Action enqueue)
        {
            if (pending is null) throw new ArgumentNullException(nameof(pending));
            if (enqueue is null) throw new ArgumentNullException(nameof(enqueue));

            if (!pending.TryAdd(itemId, 0)) return false;

            try
            {
                enqueue();
            }
            catch
            {
                pending.TryRemove(itemId, out _);
                throw;
            }

            return true;
        }

        /// <summary>
        /// Enqueues work the worker already owns (defer, permit rollback, favorite/regular queue
        /// move). The marker is kept registered so the item stays deduplicated while it waits.
        /// </summary>
        internal static void Requeue(ConcurrentDictionary<long, byte> pending, long itemId,
            Action enqueue)
        {
            if (pending is null) throw new ArgumentNullException(nameof(pending));
            if (enqueue is null) throw new ArgumentNullException(nameof(enqueue));

            pending.TryAdd(itemId, 0);
            enqueue();
        }

        /// <summary>
        /// Clears the marker after the item has been processed, skipped, failed or filtered out.
        /// Returns true when a marker was present.
        /// </summary>
        internal static bool Complete(ConcurrentDictionary<long, byte> pending, long itemId)
        {
            if (pending is null) throw new ArgumentNullException(nameof(pending));

            return pending.TryRemove(itemId, out _);
        }
    }
}
