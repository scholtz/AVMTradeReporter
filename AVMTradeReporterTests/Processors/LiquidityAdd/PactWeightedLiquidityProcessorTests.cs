using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;
using AVMTradeReporterTests.Processors.SWAP;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    /// <summary>
    /// Pact weighted pool (ARC4 contract, app 3689973907 ALGO/USDC 50/50) liquidity add / remove tests.
    /// </summary>
    public class PactWeightedLiquidityProcessorTests
    {
        [Test]
        public async Task PactWeightedLiquidityAdd()
        {
            // group: axfer LP optin, pay 12500000 ALGO to vault, axfer 81250000 USDC to vault, appl add_liquidity(txn,txn)
            // inner: appl vault, axfer 31867871 LP tokens from the pool account to the provider
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(64586890);
            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            var liq = dummyLiquidityService.list.FirstOrDefault(l => l.PoolAppId == PactWeightedSwapProcessorTests.WeightedPoolAppId);
            Assert.That(liq, Is.Not.Null, "weighted pool liquidity add was not detected");
            Assert.That(liq!.TxId, Is.EqualTo("ID7RHT6JPIS2L7DXMTDEZXMQHYOOUYL4N4PLYGDK2J6FZZMPO4LA"));
            Assert.That(liq.Direction, Is.EqualTo(LiquidityDirection.DepositLiquidity));
            Assert.That(liq.Protocol, Is.EqualTo(DEXProtocol.Pact));
            Assert.That(liq.PoolAddress, Is.EqualTo(PactWeightedSwapProcessorTests.WeightedPoolAddress));
            Assert.That(liq.AssetIdA, Is.EqualTo(0));
            Assert.That(liq.AssetAmountA, Is.EqualTo(12500000));
            Assert.That(liq.AssetIdB, Is.EqualTo(452399768));
            Assert.That(liq.AssetAmountB, Is.EqualTo(81250000));
            Assert.That(liq.AssetIdLP, Is.EqualTo(3689973910));
            Assert.That(liq.AssetAmountLP, Is.EqualTo(31867871));
            Assert.That(liq.LiquidityProvider, Is.EqualTo("PACTFIIFTBWHD52WFMKTSCDZWFVWZK4ZDSWIZHMROZLYD5PZFA4TLCWP7I"));
            Assert.That(liq.A, Is.EqualTo(12500000));
            Assert.That(liq.B, Is.EqualTo(81250000));
            Assert.That(liq.L, Is.EqualTo(31868871));
            Assert.That(liq.TxState, Is.EqualTo(TxState.Confirmed));
            Assert.That(liq.BlockId, Is.EqualTo(64586890));
        }

        [Test]
        public async Task PactWeightedLiquidityRemove()
        {
            // group: axfer 2781357977 LP tokens to the pool account, appl remove_liquidity(axfer,uint64,uint64)
            // inner: appl vault -> nested pay 1092259613 ALGO and axfer 7082528354 USDC to the provider
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(64597027);
            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            var liq = dummyLiquidityService.list.FirstOrDefault(l => l.PoolAppId == PactWeightedSwapProcessorTests.WeightedPoolAppId);
            Assert.That(liq, Is.Not.Null, "weighted pool liquidity remove was not detected");
            Assert.That(liq!.TxId, Is.EqualTo("FYES7ZZREY6L5H5KXHERFFOV53L6JGOTO6SKMM2SKE75N7UMFXYA"));
            Assert.That(liq.Direction, Is.EqualTo(LiquidityDirection.WithdrawLiquidity));
            Assert.That(liq.Protocol, Is.EqualTo(DEXProtocol.Pact));
            Assert.That(liq.PoolAddress, Is.EqualTo(PactWeightedSwapProcessorTests.WeightedPoolAddress));
            Assert.That(liq.AssetIdA, Is.EqualTo(0));
            Assert.That(liq.AssetAmountA, Is.EqualTo(1092259613));
            Assert.That(liq.AssetIdB, Is.EqualTo(452399768));
            Assert.That(liq.AssetAmountB, Is.EqualTo(7082528354));
            Assert.That(liq.AssetIdLP, Is.EqualTo(3689973910));
            Assert.That(liq.AssetAmountLP, Is.EqualTo(2781357977));
            Assert.That(liq.LiquidityProvider, Is.EqualTo("KQCO6GGB2P7BJ6IJZOXIYV7FDYA2ACXPZBFGKQIQWE4C23FCVEHJPTQJD4"));
            Assert.That(liq.A, Is.EqualTo(12515139));
            Assert.That(liq.B, Is.EqualTo(81151792));
            Assert.That(liq.L, Is.EqualTo(31868871));
            Assert.That(liq.TxState, Is.EqualTo(TxState.Confirmed));
        }
    }
}
