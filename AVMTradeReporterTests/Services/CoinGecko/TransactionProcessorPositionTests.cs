using Algorand;
using Algorand.Algod.Model.Transactions;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.SWAP;
using AVMTradeReporter.Services;
using AVMTradeReporterTests.Processors;
using Microsoft.Extensions.Logging.Abstractions;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    /// <summary>Offline checks that the block processor stamps every event with its transaction / event position.</summary>
    public class TransactionProcessorPositionTests
    {
        private sealed class StubSwapProcessor : ISwapProcessor
        {
            public string AppArg { get; set; } = "AA";
            public int Created;

            public Trade? GetTrade(SignedTransaction current, SignedTransaction? previous, Algorand.Algod.Model.Block? block, Digest? txGroup, string topTxId, Address trader, TxState tradeState)
                => new Trade { TxId = "T" + Interlocked.Increment(ref Created), TopTxId = topTxId };
        }

        private static SignedTransaction AppCall(params SignedTransaction[] inner)
        {
            var tx = new ApplicationNoopTransaction { ApplicationArgs = new List<byte[]> { new byte[] { 0xAA } } };
            return new SignedTransaction
            {
                Tx = tx,
                Detail = new Algorand.Algod.Model.Transactions.SignedTransactionDetail { InnerTxns = inner.ToList() },
            };
        }

        [Test]
        public async Task WithoutPosition_TradesAreLeftUnstamped()
        {
            var processor = new TransactionProcessor(NullLogger<TransactionProcessor>.Instance);
            processor.swapProcessors.Clear();
            processor.swapProcessors["aa"] = new StubSwapProcessor();
            var trades = new DummyTradeService();

            await processor.ProcessTransaction(AppCall(), null, null, null, null, "TOP", new Address(), TxState.Confirmed, trades, new DummyLiquidityService(), default);

            Assert.That(trades.positions, Is.EqualTo(new (ulong?, uint?)[] { (null, null) }));
        }

        private sealed class FailingSwapProcessor : ISwapProcessor
        {
            public string AppArg { get; set; } = "AA";
            public int FailuresLeft;
            public int Calls;

            public Trade? GetTrade(SignedTransaction current, SignedTransaction? previous, Algorand.Algod.Model.Block? block, Digest? txGroup, string topTxId, Address trader, TxState tradeState)
            {
                Calls++;
                if (FailuresLeft-- > 0) throw new InvalidOperationException("asset lookup hiccup");
                return new Trade { TxId = "T" + Calls, TopTxId = topTxId };
            }
        }

        [Test]
        public async Task ProcessBlock_ReportsFailedPositions_AndReprocessesOnlyThose()
        {
            var processor = new TransactionProcessor(NullLogger<TransactionProcessor>.Instance);
            processor.swapProcessors.Clear();
            var swaps = new FailingSwapProcessor { FailuresLeft = 1 };
            processor.swapProcessors["aa"] = swaps;
            var trades = new DummyTradeService();
            var block = new Algorand.Algod.Model.CertifiedBlock
            {
                Block = new Algorand.Algod.Model.Block { Round = 5, Transactions = new List<SignedTransaction> { AppCall(), AppCall(), AppCall() } },
            };

            var failed = await processor.ProcessBlock(block, trades, new DummyLiquidityService(), default);
            Assert.That(failed, Is.EqualTo(new ulong[] { 1 }), "the first transaction threw");
            Assert.That(trades.positions.Select(p => p.TxnIndex), Is.EqualTo(new ulong?[] { 2, 3 }));

            failed = await processor.ProcessBlock(block, trades, new DummyLiquidityService(), default, failed.ToHashSet());
            Assert.That(failed, Is.Empty);
            Assert.That(swaps.Calls, Is.EqualTo(4), "only the failed transaction was processed again");
            Assert.That(trades.positions.Select(p => p.TxnIndex), Is.EqualTo(new ulong?[] { 2, 3, 1 }));
        }

        [Test]
        public async Task InnerSwapsOfOneTransaction_ShareTheTxnIndexAndCountEvents()
        {
            var processor = new TransactionProcessor(NullLogger<TransactionProcessor>.Instance);
            processor.swapProcessors.Clear();
            processor.swapProcessors["aa"] = new StubSwapProcessor();
            var trades = new DummyTradeService();

            // top level app call (itself not a swap hit? it is: first arg matches) with two inner swap calls
            await processor.ProcessTransaction(AppCall(AppCall(), AppCall()), null, null, null, null, "TOP", new Address(), TxState.Confirmed, trades, new DummyLiquidityService(), default, new EventPosition(42));

            Assert.That(trades.positions, Is.EqualTo(new (ulong?, uint?)[] { (42, 0), (42, 1), (42, 2) }));
        }
    }
}
