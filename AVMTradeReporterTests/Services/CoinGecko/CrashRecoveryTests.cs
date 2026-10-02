using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using AVMTradeReporter.Services.CoinGecko;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    /// <summary>
    /// Issue #23: <c>Indexer.Round</c> is persisted when a block task STARTS, so a crash with blocks N-2..N in flight used to lose
    /// them for good. These tests replay that crash with the real tracker, the real per-block batches and the real startup rule
    /// (a fake store stands in for Elasticsearch): the persisted <c>StoredThrough</c> must stop below the first unstored block,
    /// and the restart must re-process exactly the in-flight blocks - idempotently.
    /// </summary>
    public class CrashRecoveryTests
    {
        private static IndexedBlockTracker NewTracker()
        {
            var config = new AppConfiguration();
            config.Redis.Enabled = false;
            config.CoinGecko.LatestBlockVisibilityDelaySeconds = 0;
            return new IndexedBlockTracker(Options.Create(config), NullLogger<IndexedBlockTracker>.Instance, new ServiceCollection().BuildServiceProvider());
        }

        private static Trade Swap(ulong block) => new() { BlockId = block, TxId = "TX" + block };

        /// <summary>One block task: registers its trade, closes its batch and flushes - like <c>ProcessFetchedBlockAsync</c>.</summary>
        private static async Task RunBlock(PendingBlockBatches pending, IndexedBlockTracker tracker, HashSet<string> store, ulong block, bool elasticsearchReachable = true)
        {
            pending.Open(block, 1_000 + (long)block);
            pending.Add(Swap(block));
            pending.Close(block);
            var flush = await pending.FlushAsync(
                (trades, _) =>
                {
                    if (!elasticsearchReachable) return Task.FromResult(StoreResult.Unreachable);
                    foreach (var t in trades) store.Add(t.TxId); // idempotent upsert by tx id
                    return Task.FromResult(StoreResult.AllStored);
                },
                (_, _) => Task.FromResult(StoreResult.AllStored),
                default);
            foreach (var (round, timestamp) in flush.Completed) tracker.MarkCompleted(round, timestamp);
        }

        [Test]
        public async Task CrashWithBlocksInFlight_RestartsAtTheLastStoredBlock_AndLosesNothing()
        {
            const ulong n = 1_000;
            var store = new HashSet<string>(); // "Elasticsearch"
            var pending = new PendingBlockBatches(unreachableRetryInterval: TimeSpan.Zero);
            var tracker = NewTracker();
            tracker.Seed(n - 4, 1_000 + (long)n - 4); // everything up to N-4 was stored in earlier runs

            // blocks N-3 .. N are started (Indexer.Round is persisted as N+1 right away); only N-3 and N-1 finish before the crash
            await RunBlock(pending, tracker, store, n - 3);
            await RunBlock(pending, tracker, store, n - 1);
            pending.Open(n - 2, null); // in flight: fetched, not yet registered / stored
            pending.Open(n, null);

            // the process dies here. What would be persisted:
            var persistedRound = n + 1;
            var storedThrough = tracker.CompletedThrough!.Round;
            Assert.That(storedThrough, Is.EqualTo(n - 3), "N-2 is unstored, so the contiguous watermark cannot pass it - N-1 is stored but not yet 'through'");

            // restart: the rule decides where the indexer continues
            var (seed, rewind, verified) = TradeReporterBackgroundService.ResolveStartupSeed(persistedRound, storedThrough, null, 3);
            Assert.That((seed, rewind, verified), Is.EqualTo((n - 3, true, true)));
            var restartRound = seed + 1;
            Assert.That(restartRound, Is.EqualTo(n - 2), "re-processing starts at the first block that was not stored");

            var pendingAfterRestart = new PendingBlockBatches(unreachableRetryInterval: TimeSpan.Zero);
            var trackerAfterRestart = NewTracker();
            trackerAfterRestart.Seed(seed, 1_000 + (long)seed);
            for (var block = restartRound; block <= n; block++) await RunBlock(pendingAfterRestart, trackerAfterRestart, store, block);
            // the indexer continues with new blocks
            await RunBlock(pendingAfterRestart, trackerAfterRestart, store, n + 1);

            for (var block = n - 3; block <= n + 1; block++) Assert.That(store, Does.Contain("TX" + block), $"block {block} must be stored after the restart");
            Assert.That(store.Count, Is.EqualTo(5), "N-1 was processed twice (before the crash and by the replay) - the upsert by tx id makes that harmless");
            Assert.That(trackerAfterRestart.CompletedThrough!.Round, Is.EqualTo(n + 1), "the watermark catches up completely");
        }

        [Test]
        public async Task ElasticsearchOutageAtTheCrash_ReplaysEveryParkedBlock_HoweverMany()
        {
            const ulong n = 5_000;
            var store = new HashSet<string>();
            var pending = new PendingBlockBatches(unreachableRetryInterval: TimeSpan.Zero);
            var tracker = NewTracker();
            tracker.Seed(n - 1, 1);
            // 80 blocks complete processing while Elasticsearch is down: parked in memory, the watermark must not move
            for (ulong block = n; block < n + 80; block++) await RunBlock(pending, tracker, store, block, elasticsearchReachable: false);
            Assert.That(tracker.CompletedThrough!.Round, Is.EqualTo(n - 1));
            Assert.That(store, Is.Empty);

            // crash: the parked blocks existed only in memory. The persisted StoredThrough is still n-1 although Round is n+80.
            var (seed, rewind, _) = TradeReporterBackgroundService.ResolveStartupSeed(n + 80, tracker.CompletedThrough.Round, null, 3);
            Assert.That((seed, rewind), Is.EqualTo((n - 1, true)), "no cap on the rewind: all 80 parked blocks are re-processed");
        }

        [Test]
        public async Task CleanShutdown_PersistsTheFinalWatermark_SoNothingIsReplayed()
        {
            const ulong n = 2_000;
            var store = new HashSet<string>();
            var pending = new PendingBlockBatches(unreachableRetryInterval: TimeSpan.Zero);
            var tracker = NewTracker();
            tracker.Seed(n - 1, 1);
            for (var block = n; block < n + 5; block++) await RunBlock(pending, tracker, store, block);

            var (seed, rewind, verified) = TradeReporterBackgroundService.ResolveStartupSeed(n + 5, tracker.CompletedThrough!.Round, null, 3);
            Assert.That((seed, rewind, verified), Is.EqualTo((n + 4, false, true)));
        }

        [Test]
        public void IndexerDocument_CarriesStoredThrough_OnTopOfRound()
        {
            var indexer = new Indexer { Id = "x", Round = 10, StoredThrough = 7 };
            Assert.That(System.Text.Json.JsonSerializer.Serialize(indexer), Does.Contain("\"StoredThrough\":7"), "persisted next to Round in the indexers document");
            Assert.That(new Indexer().StoredThrough, Is.Null, "documents from before the field existed deserialize to null (no rewind guess)");
        }
    }
}
