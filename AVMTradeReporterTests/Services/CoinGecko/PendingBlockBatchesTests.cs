using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Services;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    public class PendingBlockBatchesTests
    {
        private static Trade Trade(ulong block, string tx) => new() { BlockId = block, TxId = tx };
        private static Liquidity Liq(ulong block, string tx) => new() { BlockId = block, TxId = tx };

        private static Task<bool> Ok<T>(T[] _) => Task.FromResult(true);
        private static Task<bool> Fail<T>(T[] _) => Task.FromResult(false);

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
            var completed = await batches.FlushAsync(t => { stored = t; return Task.FromResult(true); }, Ok, default);

            Assert.That(completed, Is.EqualTo(new[] { (10UL, (long?)1000L) }));
            Assert.That(stored!.Select(t => t.TxId), Is.EqualTo(new[] { "A" }), "block 11's documents are not touched");
            Assert.That(batches.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public async Task FailedStore_KeepsTheBatch_AndCompletesItWithTheNextSuccessfulFlush()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, 1000);
            batches.Add(Trade(10, "A"));
            batches.Close(10);

            Assert.That(await batches.FlushAsync(Fail, Ok, default), Is.Empty);
            Assert.That(batches.PendingCount, Is.EqualTo(1));

            batches.Open(11, 1003);
            batches.Add(Trade(11, "B"));
            batches.Close(11);
            Trade[]? stored = null;
            var completed = await batches.FlushAsync(t => { stored = t; return Task.FromResult(true); }, Ok, default);

            Assert.That(stored!.Select(t => t.TxId).OrderBy(x => x), Is.EqualTo(new[] { "A", "B" }), "the failed batch is retried together with the new one");
            Assert.That(completed.Select(c => c.Round).OrderBy(r => r), Is.EqualTo(new ulong[] { 10, 11 }));
            Assert.That(batches.PendingCount, Is.Zero);
        }

        [Test]
        public async Task PartialSuccess_ForgetsTheAcknowledgedIndex_ButCompletesOnlyWhenBothSucceeded()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, 1000);
            batches.Add(Trade(10, "A"));
            batches.Add(Liq(10, "L"));
            batches.Close(10);

            Assert.That(await batches.FlushAsync(Ok, Fail, default), Is.Empty, "liquidity failed");

            var tradeStores = 0;
            var completed = await batches.FlushAsync(t => { tradeStores++; return Task.FromResult(true); }, Ok, default);
            Assert.That(tradeStores, Is.Zero, "trades were already acknowledged - not stored again");
            Assert.That(completed.Select(c => c.Round), Is.EqualTo(new ulong[] { 10 }));
        }

        [Test]
        public async Task EmptyBlock_CompletesWithoutAnyStore()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, 1000);
            batches.Close(10);
            var stores = 0;
            var completed = await batches.FlushAsync(t => { stores++; return Task.FromResult(true); }, l => { stores++; return Task.FromResult(true); }, default);
            Assert.That(stores, Is.Zero);
            Assert.That(completed, Is.EqualTo(new[] { (10UL, (long?)1000L) }));
        }

        [Test]
        public async Task Drop_ForgetsABlockThatWillNeverBeStored()
        {
            var batches = new PendingBlockBatches();
            batches.Open(10, null);
            batches.Drop(10);
            Assert.That(batches.PendingCount, Is.Zero);
            Assert.That(await batches.FlushAsync(Ok, Ok, default), Is.Empty);
        }

        [Test]
        public async Task ConcurrentFlushes_ReportEachBlockOnce()
        {
            var batches = new PendingBlockBatches();
            for (ulong b = 1; b <= 20; b++)
            {
                batches.Open(b, (long)b);
                batches.Add(Trade(b, "T" + b));
                batches.Close(b);
            }
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => batches.FlushAsync(async t => { await Task.Delay(5); return true; }, Ok, default))));
            var rounds = results.SelectMany(r => r).Select(c => c.Round).ToList();
            Assert.That(rounds.Count, Is.EqualTo(20));
            Assert.That(rounds.Distinct().Count(), Is.EqualTo(20));
        }
    }
}
