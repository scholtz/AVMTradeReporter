using AVMTradeReporter.Repository;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Pairs valued below 0.5% of an asset's biggest pair are excluded from Real and Total TVL.
    /// </summary>
    public class AggregatedPoolDustPoolTvlTests
    {
        [Test]
        public void EmptyInput_SumsToZero()
        {
            Assert.That(AggregatedPoolRepository.SumTvlExcludingDustPools(Array.Empty<(decimal, decimal)>()), Is.EqualTo((0m, 0m)));
        }

        [Test]
        public void PairBelowHalfPercentOfBiggest_IsExcludedFromBothSums()
        {
            var pairs = new[] { (500_000m, 1_000_000m), (200_000m, 400_000m), (1_500m, 3_000m) };
            var (real, total) = AggregatedPoolRepository.SumTvlExcludingDustPools(pairs);
            Assert.That(real, Is.EqualTo(700_000m));
            Assert.That(total, Is.EqualTo(1_400_000m));
        }

        [Test]
        public void PairExactlyAtThreshold_IsKept()
        {
            // 0.5% of 1,000,000 = 5,000 -> exactly at the threshold stays; below it goes.
            var pairs = new[] { (500_000m, 1_000_000m), (2_500m, 5_000m), (2_499m, 4_999m) };
            var (real, total) = AggregatedPoolRepository.SumTvlExcludingDustPools(pairs);
            Assert.That(total, Is.EqualTo(1_005_000m));
            Assert.That(real, Is.EqualTo(502_500m));
        }

        [Test]
        public void SinglePair_IsAlwaysKept()
        {
            var (real, total) = AggregatedPoolRepository.SumTvlExcludingDustPools(new[] { (0.5m, 1m) });
            Assert.That((real, total), Is.EqualTo((0.5m, 1m)));
        }

        [Test]
        public void BiggestIsDecidedByTotalValuation_NotRealValue()
        {
            // The first pair has the larger total but a tiny real side; it still defines the threshold.
            var pairs = new[] { (10m, 1_000_000m), (400m, 800m), (1m, 2_000m), (3_000m, 6_000m) };
            var (real, total) = AggregatedPoolRepository.SumTvlExcludingDustPools(pairs);
            Assert.That(total, Is.EqualTo(1_006_000m));
            Assert.That(real, Is.EqualTo(3_010m));
        }
    }
}
