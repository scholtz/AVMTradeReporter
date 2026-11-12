using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    /// <summary>
    /// The sync-speed paths of <see cref="PendingBlockBatches"/> (see CLAUDE.md, "Sync speed"): a block without documents
    /// completes without a flush, and the two indices of a flush are stored at the same time. Neither may cost a block
    /// or a document.
    /// </summary>
    public class PendingBlockBatchesFastPathTests
    {
        private static Trade Trade(ulong block, string tx) => new() { BlockId = block, TxId = tx };
        private static Liquidity Liq(ulong block, string tx) => new() { BlockId = block, TxId = tx };
        private static Task<StoreResult> Ok<T>(T[] _, bool __) => Task.FromResult(StoreResult.AllStored);
        private static Task<StoreResult> Down<T>(T[] _, bool __) => Task.FromResult(StoreResult.Unreachable);

        [Test]
        public void EmptyClosedBlock_CompletesWithoutAFlush_ExactlyOnce()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, 1000);
            Assert.That(batches.TryCompleteEmpty(10, out _), Is.False, "still being processed");
            batches.Close(10);

            Assert.That(batches.TryCompleteEmpty(10, out var timestamp), Is.True);
            Assert.That(timestamp, Is.EqualTo(1000));
            Assert.That(batches.PendingCount, Is.Zero);
            Assert.That(batches.TryCompleteEmpty(10, out _), Is.False, "a block completes once");
            Assert.That(batches.TryCompleteEmpty(11, out _), Is.False, "unknown block");
        }

        [Test]
        public async Task BlockWithDocuments_IsNeverCompletedWithoutItsFlush()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, 1000);
            batches.Add(Trade(10, "A"));
            batches.Close(10);
            batches.Open(11, 1003);
            batches.Add(Liq(11, "L"));
            batches.Close(11);

            Assert.That(batches.TryCompleteEmpty(10, out _), Is.False, "its trade is not stored");
            Assert.That(batches.TryCompleteEmpty(11, out _), Is.False, "its liquidity update is not stored");
            Assert.That(batches.PendingCount, Is.EqualTo(2));

            var flush = await batches.FlushAsync(Ok, Ok, default);
            Assert.That(flush.Completed.Select(c => c.Round).OrderBy(r => r), Is.EqualTo(new ulong[] { 10, 11 }));
        }

        [Test]
        public async Task EmptyBlock_IsReportedByTheFastPathOrByAFlush_NeverByBoth()
        {
            // an empty block closes while another block's flush is in flight: both paths see it closed and empty
            for (var run = 0; run < 200; run++)
            {
                var batches = new PendingBlockBatches();
                batches.Open(10, 1000);
                batches.Close(10);

                var flushTask = Task.Run(() => batches.FlushAsync(Ok, Ok, default));
                var fast = batches.TryCompleteEmpty(10, out _);
                var flush = await flushTask;

                Assert.That((fast ? 1 : 0) + flush.Completed.Count, Is.EqualTo(1), "the watermark hears about the block exactly once");
                Assert.That(batches.PendingCount, Is.Zero);
            }
        }

        [Test]
        public async Task NeedsRetry_TellsAnEmptyBlockThatDocumentsAreWaitingForAnotherFlush()
        {
            // Voi / testnet: trades are rare. After a failed flush the next blocks are empty for a long time - they complete
            // on the fast path, so they are what has to trigger the retry (the watermark waits for the pending block).
            var batches = new PendingBlockBatches(unreachableRetryInterval: TimeSpan.Zero);
            Assert.That(batches.NeedsRetry, Is.False);
            batches.Open(10, 1000);
            batches.Add(Trade(10, "A"));
            batches.Close(10);

            var outage = await batches.FlushAsync(Down, Ok, default);
            Assert.That(outage.Completed, Is.Empty);
            Assert.That(batches.NeedsRetry, Is.True);

            batches.Open(11, 1003);
            batches.Close(11);
            Assert.That(batches.TryCompleteEmpty(11, out _), Is.True, "the empty block itself is complete");
            Assert.That(batches.NeedsRetry, Is.True, "block 10 still waits");

            var retry = await batches.FlushAsync(Ok, Ok, default);
            Assert.That(retry.Completed.Select(c => c.Round), Is.EqualTo(new ulong[] { 10 }));
            Assert.That(batches.NeedsRetry, Is.False);
        }

        [Test]
        public async Task NeedsRetry_StaysSetForARejectedDocument_UntilItIsSettled()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, 1000);
            batches.Add(Trade(10, "BAD"));
            batches.Close(10);
            Func<Trade[], bool, Task<StoreResult>> reject = (_, _) => Task.FromResult(new StoreResult(true, new[] { "BAD" }));

            await batches.FlushAsync(reject, Ok, default, maxRejections: 2);
            Assert.That(batches.NeedsRetry, Is.True);
            var second = await batches.FlushAsync(reject, Ok, default, maxRejections: 2);
            Assert.That(second.AbandonedDocuments, Is.EqualTo(new[] { "BAD" }));
            Assert.That(batches.NeedsRetry, Is.False, "dropped: nothing waits any more");
        }

        [Test]
        public async Task QueuedFlush_ForABlockAnEarlierFlushAlreadyStored_ReturnsAtOnce()
        {
            // group commit: blocks 10 and 11 closed, the task of block 10 got the lock first and stored both
            var batches = new PendingBlockBatches();
            foreach (var round in new ulong[] { 10, 11 })
            {
                batches.Open(round, 1000);
                batches.Add(Trade(round, "T" + round));
                batches.Close(round);
            }
            batches.Open(12, 1006);
            batches.Add(Trade(12, "T12"));
            batches.Close(12); // closed while the task of block 11 was queued: it has its own task right behind

            var stores = 0;
            Func<Trade[], bool, Task<StoreResult>> store = (t, _) => { stores++; return Task.FromResult(StoreResult.AllStored); };
            var first = await batches.FlushAsync(store, Ok, default, onBehalfOf: 10);
            Assert.That(first.Completed.Select(c => c.Round).OrderBy(r => r), Is.EqualTo(new ulong[] { 10, 11, 12 }));

            batches.Open(13, 1009);
            batches.Add(Trade(13, "T13"));
            batches.Close(13);
            var queued = await batches.FlushAsync(store, Ok, default, onBehalfOf: 11);
            Assert.That(queued.Completed, Is.Empty, "its block was reported by the flush that stored it");
            Assert.That(stores, Is.EqualTo(1), "no second refresh wait on behalf of block 13, whose own task flushes it");

            var own = await batches.FlushAsync(store, Ok, default, onBehalfOf: 13);
            Assert.That(own.Completed.Select(c => c.Round), Is.EqualTo(new ulong[] { 13 }));
            Assert.That(batches.PendingCount, Is.Zero);
        }

        [Test]
        public async Task TradesAndLiquidity_AreStoredAtTheSameTime()
        {
            // each bulk waits for an Elasticsearch refresh: one after the other doubled the time behind the flush lock
            var batches = new PendingBlockBatches();
            batches.Open(10, 1000);
            batches.Add(Trade(10, "A"));
            batches.Add(Liq(10, "L"));
            batches.Close(10);

            var inFlight = 0;
            var overlapped = false;
            async Task<StoreResult> Slow<T>(T[] _, bool __)
            {
                if (Interlocked.Increment(ref inFlight) == 2) overlapped = true;
                await Task.Delay(150);
                Interlocked.Decrement(ref inFlight);
                return StoreResult.AllStored;
            }

            var watch = Stopwatch.StartNew();
            var flush = await batches.FlushAsync(Slow, Slow, default);
            watch.Stop();

            Assert.That(flush.Completed.Select(c => c.Round), Is.EqualTo(new ulong[] { 10 }));
            Assert.That(overlapped, Is.True, "the two stores overlap");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(290), "one refresh wait, not two");
        }

        [Test]
        public async Task SameTransactionAsTradeAndLiquidity_IsAnnouncedOncePerDocumentType_AndRetriedPerType()
        {
            // one transaction may be a swap and a liquidity update: the two documents share the tx id, not their fate
            var batches = new PendingBlockBatches(unreachableRetryInterval: TimeSpan.Zero);
            batches.Open(10, 1000);
            batches.Add(Trade(10, "TX"));
            batches.Add(Liq(10, "TX"));
            batches.Close(10);

            var liquidityPublish = new ConcurrentQueue<bool>();
            Func<Liquidity[], bool, Task<StoreResult>> liquidityDown = (_, publish) => { liquidityPublish.Enqueue(publish); return Task.FromResult(StoreResult.Unreachable); };
            Func<Liquidity[], bool, Task<StoreResult>> liquidityUp = (_, publish) => { liquidityPublish.Enqueue(publish); return Task.FromResult(StoreResult.AllStored); };
            var tradePublish = new ConcurrentQueue<bool>();
            Func<Trade[], bool, Task<StoreResult>> trades = (_, publish) => { tradePublish.Enqueue(publish); return Task.FromResult(StoreResult.AllStored); };

            var first = await batches.FlushAsync(trades, liquidityDown, default);
            Assert.That(first.Completed, Is.Empty);
            var second = await batches.FlushAsync(trades, liquidityUp, default);

            Assert.That(second.Completed.Select(c => c.Round), Is.EqualTo(new ulong[] { 10 }));
            Assert.That(tradePublish, Is.EqualTo(new[] { true }), "the trade was stored once and announced once");
            Assert.That(liquidityPublish, Is.EqualTo(new[] { true, false }), "the liquidity update is announced with its first send only");
        }
    }
}
