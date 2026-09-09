using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.SWAP
{
    /// <summary>
    /// Pact CLAMM (tick based concentrated liquidity) swap tests. Testnet pool app 763274703 (ALGO / USDC 10458941),
    /// funds held by vault app 763264108 (address EUKVCFT2...), pool account GMHCJSYI...
    /// </summary>
    public class PactClammSwapProcessorTests
    {
        public const ulong ClammPoolAppId = 763274703;
        public const string ClammPoolAddress = "GMHCJSYIJVRPGX2F6RJNK7J4TBJWYRU2ZBXRUWHHRZPORTE65IW6PT5HCE";
        public const string VaultAddress = "EUKVCFT2ZD2FHUJYJEHO3HUOL3AQFKHFGVHU45RQZQHCTXP3VZQEOR66JI";
        public const ulong AssetB = 10458941;

        [Test]
        public async Task PactClammSwapAlgo2Asa()
        {
            // group: [appl opt-in helper] [pay 100000 ALGO to vault] [appl pool swap(txn,uint64)]
            var block = await TestNetBlocks.FetchBlockAsync(63712395);

            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            var trade = dummyTradeService.trades.FirstOrDefault(t => t.PoolAppId == ClammPoolAppId);
            Assert.That(trade, Is.Not.Null, "CLAMM swap was not detected");
            Assert.That(trade!.TxId, Is.EqualTo("EW227GICEIDMXVEXIMB6D6QADEMXZZ27K7MIIXPZH6WOIFY2C4XQ"));
            Assert.That(trade.Protocol, Is.EqualTo(DEXProtocol.Pact));
            Assert.That(trade.PoolAddress, Is.EqualTo(ClammPoolAddress));
            Assert.That(trade.PoolAddress, Is.Not.EqualTo(VaultAddress));
            Assert.That(trade.AssetIdIn, Is.EqualTo(0));
            Assert.That(trade.AssetAmountIn, Is.EqualTo(100000));
            Assert.That(trade.AssetIdOut, Is.EqualTo(AssetB));
            Assert.That(trade.AssetAmountOut, Is.EqualTo(11526));
            Assert.That(trade.Trader, Is.EqualTo("ALGONAUTC2TCNH2DIK3CSSJZNLLRSDKVYGX7T42QF3T5DM3JIIGJFKOHEM"));
            Assert.That(trade.TradeState, Is.EqualTo(TxState.Confirmed));
            Assert.That(trade.BlockId, Is.EqualTo(63712395));
            // the CLAMM contract does not expose reserves - they are tracked incrementally by the pool repository
            Assert.That(trade.A, Is.EqualTo(0));
            Assert.That(trade.B, Is.EqualTo(0));
            Assert.That(dummyLiquidityService.list.Count(l => l.PoolAppId == ClammPoolAppId), Is.EqualTo(0));
        }

        [Test]
        public async Task PactClammSwapAlgo2Asa64139970()
        {
            // group: [pay 5000000 ALGO to vault] [appl pool swap(txn,uint64)]
            var block = await TestNetBlocks.FetchBlockAsync(64139970);

            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var dummyLiquidityService = new DummyLiquidityService();
            var dummyTradeService = new DummyTradeService();
            var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await txProcessor.ProcessBlock(block, dummyTradeService, dummyLiquidityService, cancelationTokenSource.Token);

            var trade = dummyTradeService.trades.FirstOrDefault(t => t.PoolAppId == ClammPoolAppId);
            Assert.That(trade, Is.Not.Null, "CLAMM swap was not detected");
            Assert.That(trade!.TxId, Is.EqualTo("FIX7KACKV6UQDJK7XCPB7BTEYCGWCP6YEY57LIVGBIADDDSLWZBQ"));
            Assert.That(trade.PoolAddress, Is.EqualTo(ClammPoolAddress));
            Assert.That(trade.AssetIdIn, Is.EqualTo(0));
            Assert.That(trade.AssetAmountIn, Is.EqualTo(5000000));
            Assert.That(trade.AssetIdOut, Is.EqualTo(AssetB));
            Assert.That(trade.AssetAmountOut, Is.EqualTo(572191));
            Assert.That(trade.Trader, Is.EqualTo("L7RF6SLJVI4YSKNRGYBMXVQUVXWRPEEJMXJBTCTYMVGCZGU7P7GM6UUTY4"));
        }
    }
}
