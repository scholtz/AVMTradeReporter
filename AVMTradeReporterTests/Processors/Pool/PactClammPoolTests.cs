using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using AVMTradeReporterTests.Processors;
using AVMTradeReporterTests.Processors.SWAP;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporterTests.Processors.Pool
{
    /// <summary>
    /// Pact CLAMM (tick based concentrated liquidity) pool loading tests against the testnet pool 763274703.
    /// </summary>
    public class PactClammPoolTests
    {
        [Test]
        public async Task LoadPactClammPoolAsync763274703()
        {
            var algod = TestNetBlocks.CreateAlgod();
            var logger = new LoggerFactory().CreateLogger<PactPoolProcessor>();
            var poolRepository = new MockPoolRepository();
            var processor = new PactPoolProcessor(algod, poolRepository, logger, new MockAssetRepository());
            string address = PactClammSwapProcessorTests.ClammPoolAddress;
            ulong appId = PactClammSwapProcessorTests.ClammPoolAppId;

            var pool = await processor.LoadPoolAsync(address, appId);

            Assert.That(pool, Is.Not.Null);
            Assert.That(pool.Protocol, Is.EqualTo(DEXProtocol.Pact));
            Assert.That(pool.AMMType, Is.EqualTo(AMMType.TickBasedCLAMM));
            Assert.That(pool.PoolAddress, Is.EqualTo(address));
            Assert.That(pool.PoolAppId, Is.EqualTo(appId));
            Assert.That(pool.AssetIdA, Is.EqualTo(0));
            Assert.That(pool.AssetIdB, Is.EqualTo(PactClammSwapProcessorTests.AssetB));
            // no LP token; reserves are computed from the tick word boxes at the current sqrt price
            Assert.That(pool.AssetIdLP, Is.EqualTo(0));
            Assert.That(pool.A, Is.GreaterThan(0));
            Assert.That(pool.B, Is.GreaterThan(0));
            // active liquidity at the current tick
            Assert.That(pool.L, Is.GreaterThan(0));
            Assert.That(pool.StableA, Is.Null);
            // fee_bps = 30, protocol_fee_bps = 2000 (20 % of the fee)
            Assert.That(pool.LPFee, Is.EqualTo(0.003m));
            Assert.That(pool.ProtocolFeePortion, Is.EqualTo(0.2m));
            Assert.That(pool.TickSpacing, Is.EqualTo(60));
            Assert.That(pool.CurrentTick, Is.GreaterThan(0));
            // sqrt price Q64.64 decoded to a decimal price of 1 ALGO in USDC (both 6 decimals)
            Assert.That(pool.CurrentPrice, Is.GreaterThan(0.001m));
            Assert.That(pool.CurrentPrice, Is.LessThan(100m));
            Assert.That(pool.AssetADecimals, Is.EqualTo(6));
            Assert.That(pool.AssetBDecimals, Is.EqualTo(6));

            var stored = await poolRepository.GetPoolAsync(address, CancellationToken.None);
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.AMMType, Is.EqualTo(AMMType.TickBasedCLAMM));

            // price implied by the virtual amounts must match the contract price
            var impliedPrice = pool.VirtualAmountBForPrice / pool.VirtualAmountAForPrice;
            Assert.That(impliedPrice, Is.EqualTo(pool.CurrentPrice!.Value).Within(0.000001m));
            // reserves are consistent with the price: with ~113 ALGO and ~14.5 USDC on 2026-09-12 the pool sits below 1 USDC/ALGO
            Assert.That(pool.RealAmountA, Is.GreaterThan(1m));
            Assert.That(pool.RealAmountB, Is.GreaterThan(0.1m));

            // second load recomputes the reserves from chain (event tracked values are replaced)
            stored.A = 1;
            stored.B = 1;
            var reloaded = await processor.LoadPoolAsync(address, appId);
            Assert.That(reloaded.A, Is.GreaterThan(1));
            Assert.That(reloaded.B, Is.GreaterThan(1));
            Assert.That(reloaded.AMMType, Is.EqualTo(AMMType.TickBasedCLAMM));
        }

        [Test]
        public async Task LiveGlobalStateIsInSyncWithTickMathAndPoolPrice()
        {
            // reads the raw global state (scale, current_price, low_price, high_price, current_tick, tick_spacing)
            // and checks that the loaded pool and the tick math reproduce them
            var algod = TestNetBlocks.CreateAlgod();
            var app = await algod.GetApplicationByIDAsync(PactClammSwapProcessorTests.ClammPoolAppId);
            ulong State(string key)
            {
                var b64 = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(key));
                var item = app.Params.GlobalState.FirstOrDefault(p => p.Key == b64);
                Assert.That(item, Is.Not.Null, $"global state {key} missing");
                return item!.Value.Uint;
            }
            var scale = State("scale");
            var currentPrice = State("current_price");
            var lowPrice = State("low_price");
            var highPrice = State("high_price");
            var currentTick = State("current_tick");
            var tickSpacing = State("tick_spacing");

            // prices are Q64.64 fixed point: scale must be 2^64 - 1, otherwise the conversion in TickMath is wrong
            Assert.That(scale, Is.EqualTo(ulong.MaxValue));
            const double twoPow64 = 18446744073709551616.0;

            // low_price / high_price are the sqrt prices at the boundaries of the current tick
            var lowFromTick = TickMath.TickToSqrtPrice(currentTick, tickSpacing) * twoPow64;
            var highFromTick = TickMath.TickToSqrtPrice(currentTick + 1, tickSpacing) * twoPow64;
            Assert.That(lowFromTick, Is.EqualTo((double)lowPrice).Within(0.000000001).Percent);
            Assert.That(highFromTick, Is.EqualTo((double)highPrice).Within(0.000000001).Percent);
            Assert.That(currentPrice, Is.LessThanOrEqualTo(lowPrice));
            Assert.That(currentPrice, Is.GreaterThanOrEqualTo(highPrice));

            // the loaded pool price and the price implied by the virtual amounts equal (current_price / scale)^2
            var logger = new LoggerFactory().CreateLogger<PactPoolProcessor>();
            var processor = new PactPoolProcessor(algod, new MockPoolRepository(), logger, new MockAssetRepository());
            var pool = await processor.LoadPoolAsync(PactClammSwapProcessorTests.ClammPoolAddress, PactClammSwapProcessorTests.ClammPoolAppId);
            var sqrt = (double)currentPrice / ((double)scale + 1);
            var expectedPrice = Convert.ToDecimal(sqrt * sqrt * Math.Pow(10, (double)pool.AssetADecimals!.Value - (double)pool.AssetBDecimals!.Value));
            Assert.That(pool.CurrentPrice, Is.EqualTo(expectedPrice).Within(0.0000001m));
            Assert.That(pool.CurrentTick, Is.EqualTo(currentTick));
            Assert.That(pool.TickSpacing, Is.EqualTo(tickSpacing));
            var implied = pool.VirtualAmountBForPrice / pool.VirtualAmountAForPrice;
            Assert.That(implied, Is.EqualTo(expectedPrice).Within(0.000001m));
            // the price must also lie inside the current tick's price range
            var lowP = Convert.ToDecimal(Math.Pow(lowFromTick / twoPow64, 2));
            var highP = Convert.ToDecimal(Math.Pow(highFromTick / twoPow64, 2));
            Assert.That(pool.CurrentPrice, Is.LessThanOrEqualTo(lowP));
            Assert.That(pool.CurrentPrice, Is.GreaterThanOrEqualTo(highP));
        }
    }
}
