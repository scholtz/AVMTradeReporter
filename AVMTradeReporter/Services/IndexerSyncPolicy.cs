namespace AVMTradeReporter.Services
{
    /// <summary>
    /// The forward indexer's decisions that depend on how far it is behind the chain tip. At the tip it follows the chain
    /// block by block; behind it (after an outage, a stall, a restart with a rewind) it is <i>catching up</i> and every
    /// round trip per block is throughput lost. Pure functions, so the rules are tested without a chain
    /// (<c>IndexerSyncPolicyTests</c>) and the benchmark (<c>SyncThroughputBenchmarkTests</c>) drives the same ones.
    /// </summary>
    public static class IndexerSyncPolicy
    {
        /// <summary>
        /// More than <paramref name="lagThresholdBlocks"/> blocks exist above the round the indexer is about to process.
        /// A threshold of zero or less switches the catch-up mode off.
        /// </summary>
        public static bool IsCatchingUp(ulong knownTip, ulong round, int lagThresholdBlocks)
            => lagThresholdBlocks > 0 && knownTip > round && knownTip - round > (ulong)lagThresholdBlocks;

        /// <summary>
        /// Algod has to be asked (wait-for-block-after) only when the block after <paramref name="round"/> is not known to
        /// exist yet. While the tip seen by the last answer is still far ahead, that answer already covers the round: one
        /// HTTP round trip per block saved for the whole backlog. The known tip is as old as that answer, though - the
        /// chain moved on during a long catch-up - so within <paramref name="lagThresholdBlocks"/> of it a catching-up
        /// indexer asks again before it concludes that it has caught up (it would otherwise leave and re-enter the
        /// catch-up mode once per stale tip).
        /// </summary>
        public static bool MustWaitForBlock(ulong knownTip, ulong round, bool catchingUp = false, int lagThresholdBlocks = 0)
            => knownTip <= round || (catchingUp && !IsCatchingUp(knownTip, round, lagThresholdBlocks));

        /// <summary>
        /// Block tasks that may run at once. Under memory pressure a catch-up keeps the slots of the tip: the raised
        /// concurrency is what holds more blocks in memory.
        /// </summary>
        public static int ConcurrencyLimit(bool catchingUp, int maxConcurrentTasks, int catchUpMaxConcurrentTasks, bool memoryPressure = false)
            => Math.Max(1, catchingUp && !memoryPressure ? Math.Max(maxConcurrentTasks, catchUpMaxConcurrentTasks) : maxConcurrentTasks);

        /// <summary>
        /// How long the fetch loop waits before it starts the next block of a catch-up: nothing while fewer than
        /// <paramref name="maxBlocksPerSecond"/> blocks were started in the current one-second window, the rest of the
        /// window once that many were. A cap of zero or less never waits.
        /// </summary>
        public static TimeSpan CatchUpPacingDelay(int startedInWindow, TimeSpan windowElapsed, int maxBlocksPerSecond)
        {
            if (maxBlocksPerSecond <= 0 || startedInWindow < maxBlocksPerSecond) return TimeSpan.Zero;
            var rest = TimeSpan.FromSeconds(1) - windowElapsed;
            return rest > TimeSpan.Zero ? rest : TimeSpan.Zero;
        }

        /// <summary>
        /// At the tip the indexer document is written with every block (other tooling reads <c>Round</c>). While catching
        /// up that is one Elasticsearch write per block on the fetch loop itself, so it is written every
        /// <paramref name="interval"/> only: after a crash the persisted <c>Round</c> is at most that much behind, and
        /// those blocks are re-processed (idempotent upserts).
        /// </summary>
        public static bool ShouldPersistIndexer(bool catchingUp, TimeSpan sinceLastPersist, TimeSpan interval)
            => !catchingUp || sinceLastPersist >= interval;
    }
}
