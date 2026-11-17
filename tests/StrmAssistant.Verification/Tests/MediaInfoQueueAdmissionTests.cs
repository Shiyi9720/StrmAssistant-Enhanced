using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Regression coverage for the 1.0.11 media info dedup invariants. The worker itself needs
    /// the whole Emby surface, so the admission rules live in MediaInfoQueueAdmission and are
    /// exercised here exactly as QueueManager calls them.
    /// </summary>
    internal static class MediaInfoQueueAdmissionTests
    {
        public static void Run(TestRunner runner)
        {
            EnqueueRegisters(runner);
            DuplicateIsRejected(runner);
            DeferredItemKeepsMarker(runner);
            CompleteClearsMarker(runner);
            FilteredItemIsReleased(runner);
            EnqueueFailureRollsBack(runner);
            IndependentItems(runner);
            InitializeClears(runner);
        }

        private static ConcurrentDictionary<long, byte> NewPending()
        {
            return new ConcurrentDictionary<long, byte>();
        }

        private static void EnqueueRegisters(TestRunner runner)
        {
            runner.Section("pending - enqueue registers and enqueues once");

            var pending = NewPending();
            var queue = new List<long>();

            var accepted = MediaInfoQueueAdmission.TryEnqueue(pending, 42, () => queue.Add(42));

            runner.Check("first enqueue accepted", accepted);
            runner.EqualLong("pending count after enqueue", 1, pending.Count);
            runner.EqualLong("queue count after enqueue", 1, queue.Count);
            runner.Check("marker present after enqueue", pending.ContainsKey(42));
        }

        private static void DuplicateIsRejected(TestRunner runner)
        {
            runner.Section("pending - duplicate enqueue is rejected");

            var pending = NewPending();
            var queue = new List<long>();

            MediaInfoQueueAdmission.TryEnqueue(pending, 42, () => queue.Add(42));
            var second = MediaInfoQueueAdmission.TryEnqueue(pending, 42, () => queue.Add(42));

            runner.Check("second enqueue rejected", !second);
            runner.EqualLong("queue still has one item", 1, queue.Count);
            runner.EqualLong("pending still has one marker", 1, pending.Count);
        }

        private static void DeferredItemKeepsMarker(TestRunner runner)
        {
            runner.Section("pending - requeue keeps the marker while deferred");

            var pending = NewPending();
            var queue = new List<long>();

            MediaInfoQueueAdmission.TryEnqueue(pending, 7, () => queue.Add(7));
            MediaInfoQueueAdmission.Requeue(pending, 7, () => queue.Add(7));

            runner.EqualLong("requeue enqueues again", 2, queue.Count);
            runner.EqualLong("marker kept while deferred", 1, pending.Count);

            var duplicate = MediaInfoQueueAdmission.TryEnqueue(pending, 7, () => queue.Add(7));
            runner.Check("deferred item still deduplicated", !duplicate);
            runner.EqualLong("no extra enqueue while deferred", 2, queue.Count);
        }

        private static void CompleteClearsMarker(TestRunner runner)
        {
            runner.Section("pending - processing completion clears the marker");

            var pending = NewPending();
            var queue = new List<long>();

            MediaInfoQueueAdmission.TryEnqueue(pending, 11, () => queue.Add(11));
            var cleared = MediaInfoQueueAdmission.Complete(pending, 11);

            runner.Check("complete reports the marker", cleared);
            runner.EqualLong("pending empty after completion", 0, pending.Count);

            var again = MediaInfoQueueAdmission.TryEnqueue(pending, 11, () => queue.Add(11));
            runner.Check("item can be queued again after completion", again);
            runner.EqualLong("queue has both runs", 2, queue.Count);
        }

        private static void FilteredItemIsReleased(TestRunner runner)
        {
            runner.Section("pending - filtered/dropped item releases the marker");

            var pending = NewPending();
            var queue = new List<long>();

            MediaInfoQueueAdmission.TryEnqueue(pending, 21, () => queue.Add(21));

            // QueueManager releases the marker for every item the scope/favorite/blacklist
            // filters dropped before a task was created.
            MediaInfoQueueAdmission.Complete(pending, 21);

            runner.Check("released item no longer pending", !pending.ContainsKey(21));
            runner.Check("released item can be re-admitted",
                MediaInfoQueueAdmission.TryEnqueue(pending, 21, () => queue.Add(21)));
        }

        private static void EnqueueFailureRollsBack(TestRunner runner)
        {
            runner.Section("pending - enqueue failure rolls back the marker");

            var pending = NewPending();
            var threw = false;

            try
            {
                MediaInfoQueueAdmission.TryEnqueue(pending, 33,
                    () => throw new InvalidOperationException("queue unavailable"));
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            runner.Check("enqueue exception propagates", threw);
            runner.Check("marker rolled back after failure", !pending.ContainsKey(33));

            // A failed enqueue must not permanently blacklist the item.
            var retry = MediaInfoQueueAdmission.TryEnqueue(pending, 33, () => { });
            runner.Check("item can be retried after failure", retry);
        }

        private static void IndependentItems(TestRunner runner)
        {
            runner.Section("pending - ids are independent");

            var pending = NewPending();
            var queue = new List<long>();

            MediaInfoQueueAdmission.TryEnqueue(pending, 1, () => queue.Add(1));
            MediaInfoQueueAdmission.TryEnqueue(pending, 2, () => queue.Add(2));
            MediaInfoQueueAdmission.Complete(pending, 1);

            runner.Check("completed id is gone", !pending.ContainsKey(1));
            runner.Check("other id stays pending", pending.ContainsKey(2));

            var duplicate = MediaInfoQueueAdmission.TryEnqueue(pending, 2, () => queue.Add(2));
            runner.Check("other id still deduplicated", !duplicate);
            runner.EqualLong("queue holds two items", 2, queue.Count);
        }

        private static void InitializeClears(TestRunner runner)
        {
            runner.Section("pending - Initialize clears all markers");

            var pending = NewPending();
            MediaInfoQueueAdmission.TryEnqueue(pending, 1, () => { });
            MediaInfoQueueAdmission.TryEnqueue(pending, 2, () => { });

            pending.Clear();

            runner.EqualLong("pending empty after clear", 0, pending.Count);
            runner.Check("id can be admitted after clear",
                MediaInfoQueueAdmission.TryEnqueue(pending, 1, () => { }));
        }
    }
}
