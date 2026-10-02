using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Model
{
    /// <summary>
    /// A stable-swap pool holding only a few thousand base units used to report a price of 2.0 for a 1:1 pair: the probe trade
    /// (reserve / 10000) rounded to 1 base unit and the integer curve solution lost the price. Found by the live Pact
    /// Aramid/ALGO test once its pool was drained to dust.
    /// </summary>
    public class StableSwapDustPriceTests
    {
        private static PoolModel Stable(ulong a, ulong b) => new()
        {
            AMMType = AMMType.StableSwap,
            Protocol = DEXProtocol.Pact,
            StableA = a,
            StableB = b,
            Amplifier = 5000,
            AssetADecimals = 6,
            AssetBDecimals = 6,
        };

        [TestCase(1072UL, 987UL)]
        [TestCase(10_000UL, 10_000UL)]
        [TestCase(500_000UL, 480_000UL)]
        [TestCase(5_000_000_000UL, 4_800_000_000UL)]
        public void BalancedStablePool_PricesNearOne_AtAnyDepth(ulong a, ulong b)
        {
            var pool = Stable(a, b);
            var ratio = pool.VirtualAmountB / pool.VirtualAmountA;
            Assert.That(ratio, Is.GreaterThan(0.9m).And.LessThan(1.1m), $"{a}/{b} -> {ratio}");
        }
    }
}
