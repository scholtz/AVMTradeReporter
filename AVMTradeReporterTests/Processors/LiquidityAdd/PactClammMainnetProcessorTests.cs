using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.LiquidityAdd
{
    /// <summary>
    /// Pact CLAMM on mainnet (contract of factory 3656084442, ALGO/USDC pool 3762241535). Its liquidity ABI differs from the
    /// testnet pool (mint 4d17cf3e / burn 2b3b3ec8 instead of 0ab9446c / db75d435); before it was indexed every mainnet
    /// liquidity add / remove was silently ignored and the event tracked reserves drifted (~6.9k USD TVL vs 18.93k on Pact).
    /// </summary>
    public class PactClammMainnetProcessorTests
    {
        private const ulong PoolAppId = 3762241535;
        private const string PoolAddress = "TEXF2ZPVYW55BBUKV35GCX5GC27J7FBINBYHWTKZ4RED3ZTP2SEM4EEBL4";
        private const ulong Usdc = 31566704;

        private static async Task<(DummyTradeService Trades, DummyLiquidityService Liquidity)> ProcessMainnetBlockAsync(ulong round)
        {
            var client = new Algorand.Gossip.GossipHttpClient(Algorand.Gossip.GossipHttpConfiguration.MainNetArchival);
            var block = await client.FetchBlockAsync(round);
            ILogger<TransactionProcessor> logger = new LoggerFactory().CreateLogger<TransactionProcessor>();
            var txProcessor = new TransactionProcessor(logger);
            var liquidity = new DummyLiquidityService();
            var trades = new DummyTradeService();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await txProcessor.ProcessBlock(block, trades, liquidity, cts.Token);
            return (trades, liquidity);
        }

        [Test]
        public async Task MintV2IsDetected()
        {
            // group: [appl factory] [pay box MBR to pool] [pay 115 ALGO to vault] [axfer 7.398088 USDC to vault] [appl pool 4d17cf3e(1000378, 1000413, ..)]
            // inner: appl vault deposit(0, 115000000, 31566704, 7398088), budget appl 0 calls
            var (trades, liquidity) = await ProcessMainnetBlockAsync(66824877);

            var liq = liquidity.list.SingleOrDefault(l => l.PoolAppId == PoolAppId);
            Assert.That(liq, Is.Not.Null, "mainnet CLAMM mint was not detected");
            Assert.That(liq!.TxId, Is.EqualTo("RSTTQCD22Z6FYXZNP7M6IHUCGK6JPKPEO6U66UBG44OGBCWYSSGA"));
            Assert.That(liq.Direction, Is.EqualTo(LiquidityDirection.DepositLiquidity));
            Assert.That(liq.Protocol, Is.EqualTo(DEXProtocol.Pact));
            Assert.That(liq.PoolAddress, Is.EqualTo(PoolAddress));
            Assert.That(liq.AssetIdA, Is.EqualTo(0));
            Assert.That(liq.AssetAmountA, Is.EqualTo(115_000_000));
            Assert.That(liq.AssetIdB, Is.EqualTo(Usdc));
            Assert.That(liq.AssetAmountB, Is.EqualTo(7_398_088));
            Assert.That(liq.AssetIdLP, Is.EqualTo(0));
            Assert.That(liq.LiquidityProvider, Is.EqualTo("NKARQLO6RIDHZAEG6CGP4ATUV2UHEC4C7N64LYIAX5BXLVE2PLCCHLTHCM"));
            Assert.That(liq.TxState, Is.EqualTo(TxState.Confirmed));
            Assert.That(trades.trades.Count(t => t.PoolAppId == PoolAppId), Is.EqualTo(0));
        }

        [Test]
        public async Task SingleSidedMintV2IsDetected()
        {
            // a USDC-only position below the price: [pay 1 uALGO to vault] [axfer 2500 USDC to vault] [appl pool 4d17cf3e]
            // vault deposit(0, 0, 31566704, 2500000000)
            var (_, liquidity) = await ProcessMainnetBlockAsync(66802716);

            var liq = liquidity.list.SingleOrDefault(l => l.PoolAppId == PoolAppId);
            Assert.That(liq, Is.Not.Null, "single sided mainnet CLAMM mint was not detected");
            Assert.That(liq!.TxId, Is.EqualTo("LGTOOCYPYSXJLZ2J5XA4GOOF4W3MINEDQNCOZYNVSHDMKLA77QLQ"));
            Assert.That(liq.Direction, Is.EqualTo(LiquidityDirection.DepositLiquidity));
            Assert.That(liq.AssetIdA, Is.EqualTo(0));
            Assert.That(liq.AssetAmountA, Is.EqualTo(0));
            Assert.That(liq.AssetIdB, Is.EqualTo(Usdc));
            Assert.That(liq.AssetAmountB, Is.EqualTo(2_500_000_000));
            Assert.That(liq.LiquidityProvider, Is.EqualTo("W3Y27TI4AAVNXKMG6ZU4JSY5HW3UUR2HAD7NLFDGK66TZCZXOEINLGTLNU"));
        }

        [Test]
        public async Task BurnV2IsDetected()
        {
            // [appl factory] [appl pool burn(1000387, 1000422, min A, min B)] -> inner vault withdraw -> pay 4.99999 ALGO + axfer 0.493916 USDC
            var (trades, liquidity) = await ProcessMainnetBlockAsync(66826446);

            var liq = liquidity.list.SingleOrDefault(l => l.PoolAppId == PoolAppId);
            Assert.That(liq, Is.Not.Null, "mainnet CLAMM burn was not detected");
            Assert.That(liq!.TxId, Is.EqualTo("V72L26OJRAONOULX4DZFMWAG7J4Y5RZZYCYH5M5YCVS674IQGFNQ"));
            Assert.That(liq.Direction, Is.EqualTo(LiquidityDirection.WithdrawLiquidity));
            Assert.That(liq.PoolAddress, Is.EqualTo(PoolAddress));
            Assert.That(liq.AssetIdA, Is.EqualTo(0));
            Assert.That(liq.AssetAmountA, Is.EqualTo(4_999_990));
            Assert.That(liq.AssetIdB, Is.EqualTo(Usdc));
            Assert.That(liq.AssetAmountB, Is.EqualTo(493_916));
            Assert.That(liq.LiquidityProvider, Is.EqualTo("7DDJAXXGJEKSJVSBUGSA2OUZ5MESUJITANVU4HV5H7FLPJOUTGS2YU2LHM"));
            Assert.That(trades.trades.Count(t => t.PoolAppId == PoolAppId), Is.EqualTo(0));
        }

        [Test]
        public async Task RouterSwapThroughClammIsTracked()
        {
            // 4RRJGO6F6WGE3GDYKXVSHOMAZTMW6GJVRIZTEGIKTYREWOQL6A2Q: Haystack router (app 3757715482) multi hop swap, the Pact CLAMM
            // leg is an inner pair [pay 13.6 ALGO to vault] [appl pool swap] -> vault swap -> axfer 1.219404 USDC to the router
            var (trades, _) = await ProcessMainnetBlockAsync(66846070);

            var trade = trades.trades.SingleOrDefault(t => t.PoolAppId == PoolAppId);
            Assert.That(trade, Is.Not.Null, "router swap through the CLAMM pool was not detected");
            Assert.That(trade!.TopTxId, Is.EqualTo("4RRJGO6F6WGE3GDYKXVSHOMAZTMW6GJVRIZTEGIKTYREWOQL6A2Q"));
            Assert.That(trade.Protocol, Is.EqualTo(DEXProtocol.Pact));
            Assert.That(trade.PoolAddress, Is.EqualTo(PoolAddress));
            Assert.That(trade.AssetIdIn, Is.EqualTo(0));
            Assert.That(trade.AssetAmountIn, Is.EqualTo(13_600_000));
            Assert.That(trade.AssetIdOut, Is.EqualTo(Usdc));
            Assert.That(trade.AssetAmountOut, Is.EqualTo(1_219_404));
            Assert.That(trade.PoolSqrtPriceX64, Is.EqualTo(5531894802404564755UL));
            Assert.That(trade.TradeState, Is.EqualTo(TxState.Confirmed));
        }
    
        [Test]
        public async Task SwapCrossingPriceOneCarriesTheNewTickOrigin()
        {
            // USDC/POW 3764749314: the swap of round 66737071 moved the price of USDC in POW from ~0.99 (tick 1000000, d = 1e6)
            // past 1 to ~4.8 (tick 78): its global delta sets d = 0, which flips how current_price has to be read
            var (trades, _) = await ProcessMainnetBlockAsync(66737071);

            var trade = trades.trades.SingleOrDefault(t => t.PoolAppId == 3764749314);
            Assert.That(trade, Is.Not.Null, "the CLAMM swap was not detected");
            Assert.That(trade!.PoolTickOrigin, Is.EqualTo(0));
            Assert.That(trade.PoolTick, Is.EqualTo(78));
            Assert.That(trade.PoolSqrtPriceX64, Is.EqualTo(8395360813952946615UL));
            // an ordinary swap leaves d untouched and carries no origin
            var (ordinary, _) = await ProcessMainnetBlockAsync(66846070);
            Assert.That(ordinary.trades.Single(t => t.PoolAppId == PoolAppId).PoolTickOrigin, Is.Null);
        }
}
}
