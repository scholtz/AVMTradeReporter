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
