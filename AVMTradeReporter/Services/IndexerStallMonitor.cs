using System.Collections.Concurrent;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Notices an indexer that stopped making progress inside a live process: a block task that never returns (an await
    /// that never completes - a leaked lock, a call without a timeout) or a fetch loop that stopped turning.
    /// </summary>
    /// <remarks>
    /// 2026-11 incident (Algorand mainnet), as reconstructed from the indexer document (the old pod's log was gone, the
    /// hanging await itself is unknown): one block task hung on 2026-11-07. Nothing failed and nothing was logged, but
    /// the latest-block watermark and <c>Indexer.StoredThrough</c> wait for every block, so both froze at the block
    /// before it. Three more hangs later (one per <c>MaxConcurrentTasks</c> slot, then the synchronous path) the fetch
    /// loop itself stood still, from 2026-11-11 on. The pod stayed "healthy" for days; the next deploy then had to
    /// re-process 108 826 blocks from <c>StoredThrough</c>. A hung task cannot be repaired in place (whatever it holds
    /// stays held), but a restart can: it resumes at <c>StoredThrough + 1</c>, which is exactly the hung block. This
    /// monitor is what <see cref="HealthChecks.IndexerStallHealthCheck"/> reports, so the liveness probe restarts the pod
    /// after minutes instead of never.
    /// </remarks>
    public sealed class IndexerStallMonitor
    {
        private readonly TimeProvider _time;
        private readonly ConcurrentDictionary<ulong, DateTimeOffset> _inFlight = new();
        private long _heartbeatTicks; // 0: the loop is not running (not started, stopped, or a pod that does not index)

        public IndexerStallMonitor(TimeProvider? timeProvider = null)
        {
            _time = timeProvider ?? TimeProvider.System;
        }

        /// <summary>The fetch loop is alive: called once per iteration.</summary>
        public void Heartbeat() => Interlocked.Exchange(ref _heartbeatTicks, _time.GetUtcNow().UtcTicks);

        /// <summary>The fetch loop ended on purpose (shutdown): silence is expected from here on.</summary>
        public void LoopStopped()
        {
            Interlocked.Exchange(ref _heartbeatTicks, 0);
            _inFlight.Clear();
        }

        /// <summary>Work on a block began. Starting the same block again restarts its clock.</summary>
        public void BlockStarted(ulong round) => _inFlight[round] = _time.GetUtcNow();

        /// <summary>Work on a block returned, whatever the outcome (stored, failed, handed to the lost-block retry, cancelled).</summary>
        public void BlockFinished(ulong round) => _inFlight.TryRemove(round, out _);

        /// <summary>
        /// The reason the indexer counts as stalled, or null while it does not: a block in flight for longer than
        /// <paramref name="threshold"/>, or a running loop without a heartbeat for that long. A threshold of zero or less
        /// disables the check.
        /// </summary>
        public string? DescribeStall(TimeSpan threshold)
        {
            if (threshold <= TimeSpan.Zero) return null;
            var now = _time.GetUtcNow();

            ulong? oldestRound = null;
            var oldestStart = DateTimeOffset.MaxValue;
            foreach (var kv in _inFlight)
            {
                if (kv.Value < oldestStart)
                {
                    oldestStart = kv.Value;
                    oldestRound = kv.Key;
                }
            }
            if (oldestRound != null && now - oldestStart > threshold)
            {
                return $"block {oldestRound} has been in flight for {(now - oldestStart).TotalMinutes:F0} minutes ({_inFlight.Count} block(s) in flight) - a block task hangs";
            }

            var heartbeat = Interlocked.Read(ref _heartbeatTicks);
            if (heartbeat != 0)
            {
                var silence = now - new DateTimeOffset(heartbeat, TimeSpan.Zero);
                if (silence > threshold) return $"the block fetch loop has not turned for {silence.TotalMinutes:F0} minutes";
            }
            return null;
        }
    }
}
