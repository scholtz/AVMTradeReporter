using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;

namespace AVMTradeReporterTests.Model
{
    /// <summary>
    /// Price / virtual amount behaviour of the weighted AMM pools (Pact weighted pools).
    /// Spot price in a weighted pool is (B / wB) / (A / wA).
    /// </summary>
    public class WeightedPoolTests
    {
        private static AVMTradeReporter.Models.Data.Pool MakePool(ulong a, ulong b, decimal weightA)
        {
            return new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "HREVZEBTKRERQ6L2YC3HITYK7L5Q4KK6Z52SVDYMFW5XX6WGY5X6GLDZBA",
                PoolAppId = 3689973907,
                Protocol = DEXProtocol.Pact,
                AMMType = AMMType.WeightedAMM,
                AssetIdA = 0,
                AssetADecimals = 6,
                AssetIdB = 452399768,
                AssetBDecimals = 6,
                A = a,
                B = b,
                L = 1,
                WeightA = weightA,
                WeightB = 1 - weightA,
            };
        }

        [Test]
        public void FiftyFiftyWeightedPoolBehavesLikeConstantProduct()
        {
            // reserves from the real pool 3689973907: 2094.011857 ALGO / 13753.414269 USDC
            var pool = MakePool(2094011857, 13753414269, 0.5m);

            Assert.That(pool.RealAmountA, Is.EqualTo(2094.011857m));
            Assert.That(pool.RealAmountB, Is.EqualTo(13753.414269m));
            Assert.That(pool.VirtualAmountA, Is.EqualTo(pool.RealAmountA));
            Assert.That(pool.VirtualAmountB, Is.EqualTo(pool.RealAmountB));
            var price = pool.VirtualAmountBForPrice / pool.VirtualAmountAForPrice;
            Assert.That(price, Is.EqualTo(13753.414269m / 2094.011857m));
        }

        [Test]
        public void EightyTwentyWeightedPoolPriceUsesWeights()
        {
            // 80/20 pool with 800 A and 200 B: spot price = (200/0.2)/(800/0.8) = 1
            var pool = MakePool(800_000000, 200_000000, 0.8m);

            var price = pool.VirtualAmountBForPrice / pool.VirtualAmountAForPrice;
            Assert.That(price, Is.EqualTo(1m));
            // real amounts are untouched (used for TVL)
            Assert.That(pool.RealAmountA, Is.EqualTo(800m));
            Assert.That(pool.RealAmountB, Is.EqualTo(200m));
        }

        [Test]
        public void ReverseKeepsWeightsConsistent()
        {
            var pool = MakePool(800_000000, 200_000000, 0.8m);
            var reversed = pool.Reverse();

            Assert.That(reversed.WeightA, Is.EqualTo(0.2m));
            Assert.That(reversed.WeightB, Is.EqualTo(0.8m));
            var price = pool.VirtualAmountBForPrice / pool.VirtualAmountAForPrice;
            var reversedPrice = reversed.VirtualAmountBForPrice / reversed.VirtualAmountAForPrice;
            Assert.That(reversedPrice, Is.EqualTo(1 / price));
        }

        [Test]
        public void MissingWeightFallsBackToRealAmounts()
        {
            var pool = MakePool(800_000000, 200_000000, 0.8m);
            pool.WeightA = null;
            pool.WeightB = null;

            Assert.That(pool.VirtualAmountA, Is.EqualTo(pool.RealAmountA));
            Assert.That(pool.VirtualAmountB, Is.EqualTo(pool.RealAmountB));
        }
    }
}
