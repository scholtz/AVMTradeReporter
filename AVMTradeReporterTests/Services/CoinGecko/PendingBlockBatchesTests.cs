using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    public class PendingBlockBatchesTests
    {
        private static Trade Trade(ulong block, string tx) => new() { BlockId = block, TxId = tx };
        private static Liquidity Liq(ulong block, string tx) => new() { BlockId = block, TxId = tx };

        private static Task<StoreResult> Ok<T>(T[] _, bool __) => Task.FromResult(StoreResult.AllStored);
        private static Task<StoreResult> Down<T>(T[] _, bool __) => Task.FromResult(StoreResult.Unreachable);
        

        [Test]
        public async Task OnlyClosedBatches_AreFlushed_AndCompleted()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, 1000);
            batches.Add(Trade(10, "A"));
            batches.Open(11, 1003);
            batches.Add(Trade(11, "B")); // block 11 still being processed
            batches.Close(10);

            Trade[]? stored = null;
            var flush = await batches.FlushAsync((t, _) => { stored = t; return Task.FromResult(StoreResult.AllStored); }, Ok, default);

            Assert.That(flush.Completed, Is.EqualTo(new[] { (10UL, (long?)1000L) }));
            Assert.That(flush.Unreachable, Is.False);
            Assert.That(stored!.Select(t => t.TxId), Is.EqualTo(new[] { "A" }), "block 11's documents are not touched");
            Assert.That(batches.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public async Task UnreachableStore_KeepsEverything_HoweverLong_AndCompletesWithTheNextSuccessfulFlush()
        {
            var batches = new PendingBlockBatches(unreachableRetryInterval: TimeSpan.Zero); // the cooldown is tested on its own
            batches.Open(10, 1000);
            batches.Add(Trade(10, "A"));
            batches.Close(10);

            var announced = 0;
            Func<Trade[], bool, Task<StoreResult>> down = (t, publish) => { if (publish) announced += t.Length; return Task.FromResult(StoreResult.Unreachable); };
            for (var outageFlushes = 0; outageFlushes < 50; outageFlushes++)
            {
                var outage = await batches.FlushAsync(down, Ok, default);
                Assert.That(outage.Completed, Is.Empty);
                Assert.That(outage.Unreachable, Is.True);
                Assert.That(outage.AbandonedDocuments, Is.Empty, "an outage never drops documents");
            }
            Assert.That(batches.PendingCount, Is.EqualTo(1));
            Assert.That(announced, Is.EqualTo(1), "announced to the live feed once, not on each of the 50 re-sends");

            batches.Open(11, 1003);
            batches.Add(Trade(11, "B"));
            batches.Close(11);
            var stored = new List<string>();
            var flush = await batches.FlushAsync((t, _) => { stored.AddRange(t.Select(x => x.TxId)); return Task.FromResult(StoreResult.AllStored); }, Ok, default);

            Assert.That(stored.OrderBy(x => x), Is.EqualTo(new[] { "A", "B" }), "the pending batch is retried together with the new one");
            Assert.That(flush.Completed.Select(c => c.Round).OrderBy(r => r), Is.EqualTo(new ulong[] { 10, 11 }));
            Assert.That(batches.PendingCount, Is.Zero);
        }

        [Test]
        public async Task RejectedDocument_IsRetriedAlone_ThenDropped_SoTheBlockCompletes()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, 1000);
            batches.Add(Trade(10, "GOOD"));
            batches.Add(Trade(10, "BAD"));
            batches.Close(10);

            var sent = new List<string[]>();
            var published = new List<bool>();
            Func<Trade[], bool, Task<StoreResult>> store = (t, publish) => { sent.Add(t.Select(x => x.TxId).OrderBy(x => x).ToArray()); published.Add(publish); return Task.FromResult(new StoreResult(true, new[] { "BAD" })); };

            var first = await batches.FlushAsync(store, Ok, default, maxRejections: 3);
            Assert.That(first.Completed, Is.Empty, "BAD is still pending");
            Assert.That(first.AbandonedDocuments, Is.Empty);

            var second = await batches.FlushAsync(store, Ok, default, maxRejections: 3);
            Assert.That(second.Completed, Is.Empty);
            var third = await batches.FlushAsync(store, Ok, default, maxRejections: 3);

            Assert.That(sent[0], Is.EqualTo(new[] { "BAD", "GOOD" }));
            Assert.That(sent[1], Is.EqualTo(new[] { "BAD" }), "the accepted document is never re-sent (no duplicate hub publish / volume count)");
            Assert.That(sent[2], Is.EqualTo(new[] { "BAD" }));
            Assert.That(published, Is.EqualTo(new[] { true, false, false }), "announced to the live feed with the first send only");
            Assert.That(third.AbandonedDocuments, Is.EqualTo(new[] { "BAD" }));
            Assert.That(third.Completed, Is.EqualTo(new[] { (10UL, (long?)1000L) }), "dropped after the third rejection - the watermark moves on");
            Assert.That(batches.PendingCount, Is.Zero);
        }

        [Test]
        public async Task PartialReach_SettlesTheReachedIndex_AndCompletesOnlyWhenBothDid()
        {
            var batches = new PendingBlockBatches(unreachableRetryInterval: TimeSpan.Zero); // the cooldown is tested on its own
            batches.Open(10, 1000);
            batches.Add(Trade(10, "A"));
            batches.Add(Liq(10, "L"));
            batches.Close(10);

            var partial = await batches.FlushAsync(Ok, Down, default);
            Assert.That(partial.Completed, Is.Empty, "liquidity could not be stored");
            Assert.That(partial.Unreachable, Is.True);

            var tradeStores = 0;
            var flush = await batches.FlushAsync((t, _) => { tradeStores++; return Task.FromResult(StoreResult.AllStored); }, Ok, default);
            Assert.That(tradeStores, Is.Zero, "trades were already accepted - not stored again");
            Assert.That(flush.Completed.Select(c => c.Round), Is.EqualTo(new ulong[] { 10 }));
        }

        [Test]
        public async Task LargeBacklog_IsSentInSlices()
        {
            var batches = new PendingBlockBatches();
            for (ulong b = 1; b <= 5; b++)
            {
                batches.Open(b, (long)b);
                for (var i = 0; i < 1000; i++) batches.Add(Trade(b, $"T{b}-{i}"));
                batches.Close(b);
            }
            var sizes = new List<int>();
            var flush = await batches.FlushAsync((t, _) => { sizes.Add(t.Length); return Task.FromResult(StoreResult.AllStored); }, Ok, default);
            Assert.That(sizes.Max(), Is.LessThanOrEqualTo(PendingBlockBatches.BulkChunkSize));
            Assert.That(sizes.Sum(), Is.EqualTo(5000));
            Assert.That(flush.Completed.Count, Is.EqualTo(5));
        }

        [Test]
        public async Task WhileUnreachable_TheBacklogIsNotResentWithEveryFlush()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, 1000);
            batches.Add(Trade(10, "A"));
            batches.Close(10);
            var attempts = 0;
            Func<Trade[], bool, Task<StoreResult>> down = (t, _) => { attempts++; return Task.FromResult(StoreResult.Unreachable); };

            for (var i = 0; i < 20; i++)
            {
                batches.Open(11 + (ulong)i, 1003);
                batches.Add(Trade(11 + (ulong)i, "B" + i));
                batches.Close(11 + (ulong)i);
                var outcome = await batches.FlushAsync(down, Ok, default);
                Assert.That(outcome.Unreachable || outcome.BackingOff, Is.True);
                Assert.That(outcome.Completed, Is.Empty);
            }
            Assert.That(attempts, Is.EqualTo(1), "one attempt per retry interval, not one per block");
        }

        [Test]
        public async Task EmptyBlock_CompletesWithoutAnyStore()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, 1000);
            batches.Close(10);
            var stores = 0;
            var flush = await batches.FlushAsync((t, _) => { stores++; return Task.FromResult(StoreResult.AllStored); }, (l, _) => { stores++; return Task.FromResult(StoreResult.AllStored); }, default);
            Assert.That(stores, Is.Zero);
            Assert.That(flush.Completed, Is.EqualTo(new[] { (10UL, (long?)1000L) }));
        }

        [Test]
        public async Task Drop_ForgetsABlockThatWillNeverBeStored()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, null);
            batches.Drop(10);
            Assert.That(batches.PendingCount, Is.Zero);
            Assert.That((await batches.FlushAsync(Ok, Ok, default)).Completed, Is.Empty);
        }

        [Test]
        public async Task ConcurrentFlushes_ReportEachBlockOnce_AndStoreEachDocumentOnce()
        {
            var batches = new PendingBlockBatches();
            for (ulong b = 1; b <= 20; b++)
            {
                batches.Open(b, (long)b);
                batches.Add(Trade(b, "T" + b));
                batches.Close(b);
            }
            var stored = 0;
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => batches.FlushAsync(async (t, _) => { Interlocked.Add(ref stored, t.Length); await Task.Delay(5); return StoreResult.AllStored; }, Ok, default))));
            var rounds = results.SelectMany(r => r.Completed).Select(c => c.Round).ToList();
            Assert.That(rounds.Count, Is.EqualTo(20));
            Assert.That(rounds.Distinct().Count(), Is.EqualTo(20));
            Assert.That(stored, Is.EqualTo(20), "serialized flushes never store a document twice");
        }
    }
}
