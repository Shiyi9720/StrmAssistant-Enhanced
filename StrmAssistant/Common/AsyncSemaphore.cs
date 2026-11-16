using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// A semaphore whose concurrency limit can be changed at runtime without replacing the
    /// instance. Callers keep a reference from before the change, so a Release always returns the
    /// permit to the same semaphore that granted it. This avoids the disposed-instance errors and
    /// the inflated/deflated counts that come from swapping a <see cref="SemaphoreSlim"/> while
    /// workers are in flight.
    /// </summary>
    public sealed class AsyncSemaphore
    {
        private sealed class Waiter
        {
            public readonly TaskCompletionSource<bool> Completion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public LinkedListNode<Waiter> Node;
            public CancellationTokenRegistration Registration;
            public bool RegistrationSet;

            public Task Task => Completion.Task;
        }

        private readonly object _sync = new object();
        private readonly LinkedList<Waiter> _waiters = new LinkedList<Waiter>();

        private int _limit;
        private int _inUse;

        public AsyncSemaphore(int initialLimit)
        {
            _limit = Math.Max(1, initialLimit);
        }

        public int Limit
        {
            get
            {
                lock (_sync)
                {
                    return _limit;
                }
            }
        }

        /// <summary>
        /// Number of permits that can be acquired without waiting. Never negative when the limit
        /// was lowered below the number of running holders.
        /// </summary>
        public int CurrentCount
        {
            get
            {
                lock (_sync)
                {
                    return Math.Max(0, _limit - _inUse);
                }
            }
        }

        public Task WaitAsync()
        {
            return WaitAsync(CancellationToken.None);
        }

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Waiter waiter;

            lock (_sync)
            {
                if (_inUse < _limit)
                {
                    _inUse++;
                    return Task.CompletedTask;
                }

                waiter = new Waiter();
                waiter.Node = _waiters.AddLast(waiter);
            }

            if (!cancellationToken.CanBeCanceled) return waiter.Task;

            // Register outside the lock: an already-cancelled token invokes the callback
            // synchronously, and the callback takes the same lock.
            var registration = cancellationToken.Register(state => Cancel((Waiter) state), waiter);
            var disposeRegistration = false;

            lock (_sync)
            {
                if (waiter.Node?.List == null)
                {
                    // Release()/SetLimit() already woke this waiter before the registration was
                    // stored; it could not dispose the registration itself.
                    disposeRegistration = true;
                }
                else
                {
                    waiter.Registration = registration;
                    waiter.RegistrationSet = true;
                }
            }

            if (disposeRegistration) registration.Dispose();

            return waiter.Task;
        }

        public void Release()
        {
            Waiter disposeRegistration = null;

            lock (_sync)
            {
                // Guard against an unmatched Release instead of letting the count grow without
                // bound (SemaphoreSlim would throw or inflate the permit count).
                if (_inUse <= 0) return;

                _inUse--;

                while (_inUse < _limit && _waiters.First != null)
                {
                    var node = _waiters.First;
                    _waiters.RemoveFirst();

                    var waiter = node.Value;
                    waiter.Node = null;

                    if (!waiter.Completion.TrySetResult(true)) continue;

                    _inUse++;

                    if (waiter.RegistrationSet) disposeRegistration = waiter;
                }
            }

            disposeRegistration?.Registration.Dispose();
        }

        /// <summary>
        /// Changes the limit in place. Lowering it never cancels running work; those holders
        /// simply keep their permits until they release them. Raising it immediately wakes as
        /// many waiters as the new headroom allows.
        /// </summary>
        public void SetLimit(int limit)
        {
            if (limit < 1) limit = 1;

            var disposeRegistrations = new List<Waiter>();

            lock (_sync)
            {
                _limit = limit;

                while (_inUse < _limit && _waiters.First != null)
                {
                    var node = _waiters.First;
                    _waiters.RemoveFirst();

                    var waiter = node.Value;
                    waiter.Node = null;

                    if (!waiter.Completion.TrySetResult(true)) continue;

                    _inUse++;

                    if (waiter.RegistrationSet) disposeRegistrations.Add(waiter);
                }
            }

            foreach (var waiter in disposeRegistrations)
            {
                waiter.Registration.Dispose();
            }
        }

        private void Cancel(Waiter waiter)
        {
            var removed = false;

            lock (_sync)
            {
                if (waiter.Node?.List != null)
                {
                    _waiters.Remove(waiter.Node);
                    waiter.Node = null;
                    removed = true;
                }
            }

            // If Release() won the race the completion is already set to true and the waiter owns
            // a permit; do not overwrite that result with cancellation.
            if (removed)
            {
                waiter.Completion.TrySetCanceled();
            }
        }
    }
}
