using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Pairs valued (by trusted side) below 0.5% of an asset's biggest pair are excluded from Real and Total TVL.
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
        public void BiggestIsDecidedByTrustedSide_SoALopsidedPairCannotInflateTheThreshold()
        {
            // The first pair has a huge total (asset-side balance) but a small trusted side: it must not
            // set a $5M threshold that wipes out the genuine $200k pair.
            var pairs = new[] { (100_000m, 10_000_000m), (200_000m, 400_000m), (400m, 800m) };
            var (real, total) = AggregatedPoolRepository.SumTvlExcludingDustPools(pairs);
            Assert.That(real, Is.EqualTo(300_000m));
            Assert.That(total, Is.EqualTo(10_400_000m));
        }

        [Test]
        public void ZeroValuedPairs_SumToZero()
        {
            Assert.That(AggregatedPoolRepository.SumTvlExcludingDustPools(new[] { (0m, 0m), (0m, 0m) }), Is.EqualTo((0m, 0m)));
        }

        [Test]
        public async Task RecomputeAssetStats_ExcludesDustPairsFromAssetTvl()
        {
            const ulong Ref2 = 1134696561UL, Ref3 = 1185173782UL; // trusted references in AppConfiguration defaults; pools keep each at $1 given ALGO = $0.10 (from the USDC pair)
            var usdc = new AppConfiguration().UsdReferenceAssetId;
            var assets = new MockAssetRepository();
            foreach (var id in new[] { usdc, Ref2, Ref3 })
            {
                var a = (await assets.GetAssetAsync(id))!;
                a.PriceUSD = 1m;
                await assets.SetAssetAsync(a);
            }
            var repository = new AggregatedPoolRepository(null!, new LoggerFactory().CreateLogger<AggregatedPoolRepository>(), null!,
                Options.Create(new AppConfiguration()), null, null, assets);

            PoolModel Pool(ulong other, ulong algoAmount, ulong otherAmount) => new()
            {
                PoolAddress = "pool-" + other, AssetIdA = 0UL, AssetIdB = other, AssetADecimals = 6, AssetBDecimals = 6,
                A = algoAmount, B = otherAmount, Timestamp = DateTimeOffset.UtcNow,
            };
            await repository.UpdateForPairAsync(0UL, usdc, new[] { Pool(usdc, 1_000_000_000_000, 100_000_000_000) });     // $100,000 - biggest
            await repository.UpdateForPairAsync(0UL, Ref3, new[] { Pool(Ref3, 10_000_000_000, 1_000_000_000) });       // $1,000 >= $500 threshold - kept
            var before = (await assets.GetAssetAsync(0UL))!;
            var realBefore = before.TVL_USD;
            var totalBefore = before.TotalTVLAssetInUSD;
            Assert.That(realBefore, Is.GreaterThan(100_000m), "both the big and the $1,000 pair count");

            await repository.UpdateForPairAsync(0UL, Ref2, new[] { Pool(Ref2, 1_000_000_000, 100_000_000) });         // $100 < $500 threshold - dust

            var algo = (await assets.GetAssetAsync(0UL))!;
            Assert.That(algo.TVL_USD, Is.EqualTo(realBefore), "the dust pair must not change Real TVL");
            Assert.That(algo.TotalTVLAssetInUSD, Is.EqualTo(totalBefore), "the dust pair must not change Total TVL");
        }
    }
}
