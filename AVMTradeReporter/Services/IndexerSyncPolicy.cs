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
        /// exist yet. While the tip seen by the last answer is still ahead, that answer already covers the round: one
        /// HTTP round trip per block saved for the whole backlog.
        /// </summary>
        public static bool MustWaitForBlock(ulong knownTip, ulong round) => knownTip <= round;

        /// <summary>Block tasks that may run at once.</summary>
        public static int ConcurrencyLimit(bool catchingUp, int maxConcurrentTasks, int catchUpMaxConcurrentTasks)
            => Math.Max(1, catchingUp ? Math.Max(maxConcurrentTasks, catchUpMaxConcurrentTasks) : maxConcurrentTasks);

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
