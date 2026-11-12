using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AVMTradeReporterTests.Performance
{
    /// <summary>
    /// Catch-up throughput of the forward indexer (blocks per second while it is behind the chain tip), on a model of
    /// the block pipeline with the latencies measured on Algorand mainnet on 2026-11-12, when a restart had to re-process
    /// 108 826 blocks and managed 5.2 blocks/s (see CLAUDE.md, "Sync speed"):
    /// <list type="bullet">
    /// <item>fetching a block from the archival algod: ~60 ms;</item>
    /// <item>a bulk request: ~700 ms - it waits for the next Elasticsearch refresh (<c>Refresh.WaitFor</c>);</item>
    /// <item>the fetch loop itself, per block: ~30 ms (wait-for-block + writing the indexer document);</item>
    /// <item>about one block in seven holds a DEX event.</item>
    /// </list>
    /// The model runs the real <see cref="PendingBlockBatches"/> and the real <see cref="IndexerSyncPolicy"/>; only algod
    /// and Elasticsearch are delays. All latencies are scaled down by <see cref="Scale"/> to keep the test short; the
    /// reported blocks/s are scaled back. It runs in CI: a change that makes blocks queue behind the flush lock again,
    /// breaks the group commit or changes what the policy decides fails here.
    /// <para>
    /// What it does NOT cover: the loop below is a copy of the one in <c>TradeReporterBackgroundService</c>, which cannot
    /// run without algod and Elasticsearch. A round trip put back on the service's own loop does not show up here - when
    /// the service loop changes, change this copy with it (and measure a real catch-up, see CLAUDE.md).
    /// </para>
    /// </summary>
    [Category("Performance")]
    [NonParallelizable]
    public class SyncThroughputBenchmarkTests
    {
        private const int Scale = 4;
        private static readonly TimeSpan Fetch = TimeSpan.FromMilliseconds(60.0 / Scale);
        private static readonly TimeSpan Bulk = TimeSpan.FromMilliseconds(700.0 / Scale);
        private static readonly TimeSpan LoopRoundTrips = TimeSpan.FromMilliseconds(30.0 / Scale);
        private const int TradeEvery = 7;

        private sealed record Result(double BlocksPerSecond, int Flushes, int Completed, bool Contiguous);

        /// <param name="legacy">The pipeline before 2026-11: MaxConcurrentTasks slots, two round trips per block on the loop, every block flushes.</param>
        /// <param name="maxBlocksPerSecond">The catch-up pacing (null: the default of the configuration, 0: no cap).</param>
        private static async Task<Result> RunAsync(int blocks, bool legacy, int? maxBlocksPerSecond = null)
        {
            var config = new BlockProcessingConfiguration();
            if (maxBlocksPerSecond != null) config.CatchUpMaxBlocksPerSecond = maxBlocksPerSecond.Value;
            var paceWindowStart = TimeSpan.Zero;
            var paceStarted = 0;
            var pending = new PendingBlockBatches();
            var completed = new ConcurrentDictionary<ulong, byte>();
            var flushes = 0;
            const ulong first = 1_000;
            var tip = first + (ulong)blocks; // the whole backlog exists already
            var limit = legacy
                ? config.MaxConcurrentTasks
                : IndexerSyncPolicy.ConcurrencyLimit(IndexerSyncPolicy.IsCatchingUp(tip, first, config.CatchUpLagBlocks), config.MaxConcurrentTasks, config.CatchUpMaxConcurrentTasks);
            var inFlight = 0;
            var tasks = new List<Task>();

            async Task<StoreResult> StoreAsync<T>(T[] documents, bool publish)
            {
                Interlocked.Increment(ref flushes);
                await Task.Delay(Bulk);
                return StoreResult.AllStored;
            }

            async Task FlushAsync(ulong? onBehalfOf = null)
            {
                var flush = await pending.FlushAsync(StoreAsync, StoreAsync, default, onBehalfOf: onBehalfOf);
                foreach (var (round, _) in flush.Completed) completed[round] = 0;
            }

            async Task BlockAsync(ulong round)
            {
                try
                {
                    pending.Open(round, null);
                    await Task.Delay(Fetch);
                    if (round % TradeEvery == 0) pending.Add(new Trade { BlockId = round, TxId = "T" + round });
                    if (round % (TradeEvery * 5) == 0) pending.Add(new Liquidity { BlockId = round, TxId = "L" + round });
                    pending.Close(round);
                    if (legacy)
                    {
                        await FlushAsync();
                    }
                    else if (pending.TryCompleteEmpty(round, out _))
                    {
                        completed[round] = 0;
                        if (pending.NeedsRetry) await FlushAsync();
                    }
                    else
                    {
                        await FlushAsync(round);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            }

            var watch = Stopwatch.StartNew();
            var lastPersist = TimeSpan.MinValue;
            var wasCatchingUp = false;
            for (var round = first; round < tip; round++)
            {
                if (legacy)
                {
                    await Task.Delay(LoopRoundTrips); // wait-for-block + indexer document, for every block
                }
                else
                {
                    var roundTrips = TimeSpan.Zero;
                    if (IndexerSyncPolicy.MustWaitForBlock(tip, round, wasCatchingUp, config.CatchUpLagBlocks)) roundTrips += LoopRoundTrips / 2;
                    var catchingUp = wasCatchingUp = IndexerSyncPolicy.IsCatchingUp(tip, round, config.CatchUpLagBlocks);
                    var sinceLastPersist = lastPersist == TimeSpan.MinValue ? TimeSpan.MaxValue : (watch.Elapsed - lastPersist) * Scale;
                    if (IndexerSyncPolicy.ShouldPersistIndexer(catchingUp, sinceLastPersist, TimeSpan.FromSeconds(config.CatchUpIndexerPersistSeconds)))
                    {
                        roundTrips += LoopRoundTrips / 2;
                        lastPersist = watch.Elapsed;
                    }
                    if (roundTrips > TimeSpan.Zero) await Task.Delay(roundTrips);
                    limit = IndexerSyncPolicy.ConcurrencyLimit(catchingUp, config.MaxConcurrentTasks, config.CatchUpMaxConcurrentTasks);
                }

                if (!legacy && wasCatchingUp)
                {
                    // catching up, the loop waits for a slot instead of processing the block itself (as the service does)
                    while (Volatile.Read(ref inFlight) >= limit) await Task.WhenAny(tasks.Where(t => !t.IsCompleted).DefaultIfEmpty(Task.CompletedTask));
                    tasks.RemoveAll(t => t.IsCompleted);

                    // paced like the service (PaceCatchUpAsync); model time runs Scale times faster than the wall clock
                    var paceElapsed = (watch.Elapsed - paceWindowStart) * Scale;
                    if (paceElapsed >= TimeSpan.FromSeconds(1)) { paceWindowStart = watch.Elapsed; paceStarted = 0; paceElapsed = TimeSpan.Zero; }
                    var pace = IndexerSyncPolicy.CatchUpPacingDelay(paceStarted, paceElapsed, config.CatchUpMaxBlocksPerSecond);
                    if (pace > TimeSpan.Zero)
                    {
                        await Task.Delay(pace / Scale);
                        paceWindowStart = watch.Elapsed;
                        paceStarted = 0;
                    }
                    paceStarted++;
                }
                Interlocked.Increment(ref inFlight);
                if (Volatile.Read(ref inFlight) <= limit)
                {
                    tasks.Add(BlockAsync(round));
                }
                else
                {
                    await BlockAsync(round); // at the tip (and before): every slot is busy, the loop processes the block itself
                }
            }
            await Task.WhenAll(tasks);
            watch.Stop();

            var contiguous = Enumerable.Range(0, blocks).All(i => completed.ContainsKey(first + (ulong)i));
            return new Result(blocks / watch.Elapsed.TotalSeconds / Scale, flushes, completed.Count, contiguous);
        }

        [Test]
        public async Task CatchUp_IsSeveralTimesFasterThanTheBlockByBlockPipeline()
        {
            var before = await RunAsync(blocks: 210, legacy: true);
            var uncapped = await RunAsync(blocks: 840, legacy: false, maxBlocksPerSecond: 0);
            var after = await RunAsync(blocks: 840, legacy: false);
            var cap = new BlockProcessingConfiguration().CatchUpMaxBlocksPerSecond;

            TestContext.Out.WriteLine($"catch-up throughput at mainnet latencies (fetch 60 ms, bulk 700 ms, 1 in {TradeEvery} blocks with events):");
            TestContext.Out.WriteLine($"  before:   {before.BlocksPerSecond,6:F1} blocks/s ({before.Flushes} bulk requests for {before.Completed} blocks)");
            TestContext.Out.WriteLine($"  uncapped: {uncapped.BlocksPerSecond,6:F1} blocks/s ({uncapped.Flushes} bulk requests for {uncapped.Completed} blocks)");
            TestContext.Out.WriteLine($"  after:    {after.BlocksPerSecond,6:F1} blocks/s with the default cap of {cap} blocks/s ({after.Flushes} bulk requests for {after.Completed} blocks)");
            TestContext.Out.WriteLine($"  speed-up: {after.BlocksPerSecond / before.BlocksPerSecond:F1}x");

            Assert.That(before.Contiguous && uncapped.Contiguous && after.Contiguous, Is.True, "every block completes, with or without events");
            Assert.That(after.BlocksPerSecond, Is.LessThanOrEqualTo(cap * 1.1), "the cap holds: a public algod answers 429 above its rate limit");
            // measured: 6.6 -> 55 blocks/s (8x). Only the ratio is asserted, with a budget far below it: both runs slow down
            // together on a loaded CI runner, an absolute blocks/s floor would not.
            Assert.That(after.BlocksPerSecond, Is.GreaterThan(before.BlocksPerSecond * 3), "catching up must not fall back to the block-by-block pace");
        }
    }
}
