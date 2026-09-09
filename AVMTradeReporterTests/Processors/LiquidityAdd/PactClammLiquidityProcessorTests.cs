using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;
using AVMTradeReporterTests.Processors.SWAP;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    /// <summary>
    /// Pact CLAMM (tick based concentrated liquidity) mint / burn tests on the testnet pool 763274703.
    /// </summary>
    public class PactClammLiquidityProcessorTests
    {
        [Test]
        public async Task PactClammMint()
        {
            // group: [appl helper] [appl helper] [pay 1000000 ALGO to vault] [axfer 97005 USDC to vault] [appl pool mint(tick_low, tick_high, txn, txn)]
            // inner: budget appl 0 calls, appl vault deposit(0, 999995, 10458941, 97004)
            var block = await TestNetBlocks.FetchBlockAsync(63712369);
            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            var liq = dummyLiquidityService.list.FirstOrDefault(l => l.PoolAppId == PactClammSwapProcessorTests.ClammPoolAppId);
            Assert.That(liq, Is.Not.Null, "CLAMM mint was not detected");
            Assert.That(liq!.TxId, Is.EqualTo("PE6246F7UZ5WTXJI355XT2E4GM4EH6I5FLM3WWUQSBI6BXCVIN6Q"));
            Assert.That(liq.Direction, Is.EqualTo(LiquidityDirection.DepositLiquidity));
            Assert.That(liq.Protocol, Is.EqualTo(DEXProtocol.Pact));
            Assert.That(liq.PoolAddress, Is.EqualTo(PactClammSwapProcessorTests.ClammPoolAddress));
            Assert.That(liq.AssetIdA, Is.EqualTo(0));
            Assert.That(liq.AssetAmountA, Is.EqualTo(999995));
            Assert.That(liq.AssetIdB, Is.EqualTo(PactClammSwapProcessorTests.AssetB));
            Assert.That(liq.AssetAmountB, Is.EqualTo(97004));
            // no LP token - positions are boxes
            Assert.That(liq.AssetIdLP, Is.EqualTo(0));
            Assert.That(liq.AssetAmountLP, Is.EqualTo(0));
            Assert.That(liq.LiquidityProvider, Is.EqualTo("ALGONAUTC2TCNH2DIK3CSSJZNLLRSDKVYGX7T42QF3T5DM3JIIGJFKOHEM"));
            Assert.That(liq.A, Is.EqualTo(0));
            Assert.That(liq.B, Is.EqualTo(0));
            Assert.That(liq.TxState, Is.EqualTo(TxState.Confirmed));
            Assert.That(liq.BlockId, Is.EqualTo(63712369));
            Assert.That(dummyTradeService.trades.Count(t => t.PoolAppId == PactClammSwapProcessorTests.ClammPoolAppId), Is.EqualTo(0));
        }

        [Test]
        public async Task PactClammBurn()
        {
            // group: [appl helper] [appl helper] [appl pool burn(tick_low, tick_high)]
            // inner: budget appl 0 calls, appl vault withdraw -> nested pay 999987 ALGO and axfer 107608 USDC to the provider
            var block = await TestNetBlocks.FetchBlockAsync(63742787);
            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            var liq = dummyLiquidityService.list.FirstOrDefault(l => l.PoolAppId == PactClammSwapProcessorTests.ClammPoolAppId);
            Assert.That(liq, Is.Not.Null, "CLAMM burn was not detected");
            Assert.That(liq!.TxId, Is.EqualTo("N3PLTOHJYQOFGPQB3XD54ZKJG6D2PGOVY6QOZZCKM3XVLDXG3UXQ"));
            Assert.That(liq.Direction, Is.EqualTo(LiquidityDirection.WithdrawLiquidity));
            Assert.That(liq.Protocol, Is.EqualTo(DEXProtocol.Pact));
            Assert.That(liq.PoolAddress, Is.EqualTo(PactClammSwapProcessorTests.ClammPoolAddress));
            Assert.That(liq.AssetIdA, Is.EqualTo(0));
            Assert.That(liq.AssetAmountA, Is.EqualTo(999987));
            Assert.That(liq.AssetIdB, Is.EqualTo(PactClammSwapProcessorTests.AssetB));
            Assert.That(liq.AssetAmountB, Is.EqualTo(107608));
            Assert.That(liq.AssetIdLP, Is.EqualTo(0));
            Assert.That(liq.AssetAmountLP, Is.EqualTo(0));
            Assert.That(liq.LiquidityProvider, Is.EqualTo("TJ5EU4P7O447IGHP2PLSMOXQT7HD5CUAE7SQS7Z7CLFRJO47WPC3QCCQ5E"));
            Assert.That(liq.TxState, Is.EqualTo(TxState.Confirmed));
        }
    }
}
