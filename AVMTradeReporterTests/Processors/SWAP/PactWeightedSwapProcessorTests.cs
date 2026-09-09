using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.SWAP
{
    /// <summary>
    /// Pact weighted pool (ARC4 contract, app 3689973907 ALGO/USDC 50/50) swap tests.
    /// The pool funds are held by the shared vault app 3656084807 (address 7FSDP27M...),
    /// the pool identity is the pool application account HREVZEBT...
    /// </summary>
    public class PactWeightedSwapProcessorTests
    {
        public const ulong WeightedPoolAppId = 3689973907;
        public const string WeightedPoolAddress = "HREVZEBTKRERQ6L2YC3HITYK7L5Q4KK6Z52SVDYMFW5XX6WGY5X6GLDZBA";
        public const string VaultAddress = "7FSDP27MVWXOUT55I2GNQIEF6OFDFWFJBD3GIZQ4VJHZHXCLFOG2X3PZGM";

        [Test]
        public async Task PactWeightedSwapAsa2Algo()
        {
            // block contains: classic pact swap ALGO->USDC on app 2757544466 followed by weighted pool swap USDC->ALGO on app 3689973907
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(64651048);

            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            var trade = dummyTradeService.trades.FirstOrDefault(t => t.PoolAppId == WeightedPoolAppId);
            Assert.That(trade, Is.Not.Null, "weighted pool swap was not detected");
            Assert.That(trade!.TxId, Is.EqualTo("AMZBBDC5GI5X3BYORHQJBQQBIE55QAKC7PIMAJKRV7TUYCFFTCTQ"));
            Assert.That(trade.Protocol, Is.EqualTo(DEXProtocol.Pact));
            // the pool must not be identified by the shared vault address
            Assert.That(trade.PoolAddress, Is.EqualTo(WeightedPoolAddress));
            Assert.That(trade.PoolAddress, Is.Not.EqualTo(VaultAddress));
            Assert.That(trade.AssetIdIn, Is.EqualTo(452399768));
            Assert.That(trade.AssetAmountIn, Is.EqualTo(23499008));
            Assert.That(trade.AssetIdOut, Is.EqualTo(0));
            // output is sent from the vault as nested inner transaction
            Assert.That(trade.AssetAmountOut, Is.EqualTo(3607960));
            Assert.That(trade.Trader, Is.EqualTo("FUCKSHITQQUOE3Z5RSLKI7CEGHSDBFJBWMJCVOY4HFWNGW3OYJULOILKLY"));
            Assert.That(trade.TradeState, Is.EqualTo(TxState.Confirmed));
            // reserves from global state delta reserve_a / reserve_b
            Assert.That(trade.A, Is.EqualTo(1397634483));
            Assert.That(trade.B, Is.EqualTo(9117323945));
            Assert.That(trade.BlockId, Is.EqualTo(64651048));

            // classic pact pool in the same group must still be processed
            var classic = dummyTradeService.trades.FirstOrDefault(t => t.PoolAppId == 2757544466);
            Assert.That(classic, Is.Not.Null);
            Assert.That(classic!.AssetIdIn, Is.EqualTo(0));
            Assert.That(classic.AssetIdOut, Is.EqualTo(452399768));
            Assert.That(classic.AssetAmountOut, Is.EqualTo(23501358));
        }

        [Test]
        public async Task PactWeightedSwapAlgo2AsaThroughRouterInnerTx()
        {
            // router (aggregator) transaction with the weighted pool swap executed as inner transaction:
            // inner pay 3274328 ALGO to vault, inner appl 3689973907 swap -> vault sends 21288551 USDC to the router
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(64652142);

            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            var trade = dummyTradeService.trades.FirstOrDefault(t => t.PoolAppId == WeightedPoolAppId);
            Assert.That(trade, Is.Not.Null, "weighted pool swap executed as inner transaction was not detected");
            // note: the top level router tx D3QGHRSPAB7M76FAL3SI64NZIFLLGC3KIF7IPL3BA6CYV54ADSDQ uses box references
            // and the SDK computes a different TxID for it, so TopTxId is not asserted here
            Assert.That(trade!.BlockId, Is.EqualTo(64652142));
            Assert.That(trade.Protocol, Is.EqualTo(DEXProtocol.Pact));
            Assert.That(trade.PoolAddress, Is.EqualTo(WeightedPoolAddress));
            Assert.That(trade.AssetIdIn, Is.EqualTo(0));
            Assert.That(trade.AssetAmountIn, Is.EqualTo(3274328));
            Assert.That(trade.AssetIdOut, Is.EqualTo(452399768));
            Assert.That(trade.AssetAmountOut, Is.EqualTo(21288551));
            Assert.That(trade.A, Is.GreaterThan(0));
            Assert.That(trade.B, Is.GreaterThan(0));
        }
    }
}
