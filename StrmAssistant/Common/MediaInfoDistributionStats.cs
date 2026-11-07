using System.Threading;

namespace StrmAssistant.Common
{
    public enum MediaInfoFallbackReason
    {
        None,
        NoWorker,
        Timeout,
        WorkerError,
        PathNotAllowed
    }

    public sealed class MediaInfoDistributionAttempt
    {
        public bool Success { get; set; }

        public bool Attempted { get; set; }

        public MediaInfoFallbackReason FallbackReason { get; set; }
    }

    /// <summary>
    /// Per-task counters shared by distributed extraction operations. Counters are updated from
    /// the master's item workers and are safe to read after Task.WhenAll completes.
    /// </summary>
    public class DistributionStats
    {
        private int _localCount;
        private int _workerCount;
        private int _noWorkerCount;
        private int _timeoutCount;
        private int _workerErrorCount;
        private int _pathNotAllowedCount;

        public int LocalCount => Volatile.Read(ref _localCount);

        public int WorkerCount => Volatile.Read(ref _workerCount);

        public int NoWorkerCount => Volatile.Read(ref _noWorkerCount);

        public int TimeoutCount => Volatile.Read(ref _timeoutCount);

        public int WorkerErrorCount => Volatile.Read(ref _workerErrorCount);

        public int PathNotAllowedCount => Volatile.Read(ref _pathNotAllowedCount);

        public int FallbackCount => NoWorkerCount + TimeoutCount + WorkerErrorCount + PathNotAllowedCount;

        public void RecordLocal()
        {
            Interlocked.Increment(ref _localCount);
        }

        public void RecordWorker()
        {
            Interlocked.Increment(ref _workerCount);
        }

        public void RecordFallback(MediaInfoFallbackReason reason)
        {
            switch (reason)
            {
                case MediaInfoFallbackReason.NoWorker:
                    Interlocked.Increment(ref _noWorkerCount);
                    break;
                case MediaInfoFallbackReason.Timeout:
                    Interlocked.Increment(ref _timeoutCount);
                    break;
                case MediaInfoFallbackReason.WorkerError:
                    Interlocked.Increment(ref _workerErrorCount);
                    break;
                case MediaInfoFallbackReason.PathNotAllowed:
                    Interlocked.Increment(ref _pathNotAllowedCount);
                    break;
            }
        }

        public string ToLogMessage()
        {
            return "Local: " + LocalCount + ", Worker: " + WorkerCount + ", Fallbacks: " + FallbackCount +
                   " (no_worker: " + NoWorkerCount + ", timeout: " + TimeoutCount +
                   ", worker_error: " + WorkerErrorCount + ", path_not_allowed: " + PathNotAllowedCount + ")";
        }
    }

    /// <summary>
    /// Historical media-info stats name. Kept so callers and log strings do not change.
    /// </summary>
    public sealed class MediaInfoExtractionStats : DistributionStats
    {
    }
}
