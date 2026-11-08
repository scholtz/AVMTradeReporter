using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.DTO.TVL;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Regression for the "stripy Real TVL 7d chart" (ALGO/USDC hourly TVL candles swinging 5-10%
    /// every hour). Root cause: the TVL OHLC tick of an asset recompute was stamped with the
    /// aggregated pair's <c>LastUpdated</c> (the pair's newest pool timestamp = its last trade)
    /// instead of the moment the TVL was computed. <c>VolumeUpdateBackgroundService</c> re-stores
    /// EVERY pair (also pairs untouched for days or months) in a full sweep, and ALGO and the USD
    /// reference asset are recomputed on every aggregated-pool update, so each sweep dropped the
    /// CURRENT ALGO/USDC TVL into thousands of historical hourly buckets (observed tick timestamps
    /// 6 days, 33 days and 452 days old). A TVL figure is "as of now" - its tick must carry the
    /// compute time no matter how stale the triggering pair is.
    /// </summary>
    public class AggregatedPoolTvlTickTimestampTests
    {
        private const ulong ALGO = 0UL;
        // Never hardcode the per-network USD reference asset id: read the configured one.
        private static readonly ulong USDC = new AppConfiguration().UsdReferenceAssetId;

        // 0 = a pair that just traded (normal live path); 400 = a dormant pair a full volume sweep re-stores.
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(400)]
        public async Task PairUpdate_StampsTvlTickWithComputeTime_NotThePairsLastTradeTime(int pairAgeDays)
        {
            var (repository, pool, ticks) = await CreateScenarioAsync(pairAgeDays);
            var staleTradeTime = pool.Timestamp!.Value;

            var before = DateTimeOffset.UtcNow;
            await repository.UpdateForPairAsync(ALGO, USDC, new[] { pool });
            var after = DateTimeOffset.UtcNow;

            // Both always-affected series: ALGO (trusted USDC side = $100k) and the USD reference asset
            // (trusted ALGO side = 1M ALGO x $0.10 = $100k).
            foreach (var assetId in new[] { ALGO, USDC })
            {
                var assetTicks = ticks.Where(t => t.AssetId == assetId).ToList();
                Assert.That(assetTicks, Is.Not.Empty, $"the recompute must emit a TVL tick for asset {assetId} for this test to be meaningful");
                foreach (var tick in assetTicks)
                {
                    Assert.That(tick.Timestamp, Is.InRange(before.ToUnixTimeSeconds() - 1, after.ToUnixTimeSeconds() + 1),
                        "A TVL tick is the TVL as of the moment it was computed. Stamping it with the pair's last-trade time " +
                        $"({staleTradeTime:O}) writes today's TVL into a historical candle and makes the 7d TVL chart stripy (asset {assetId}).");
                }
            }
        }

        [Test]
        public async Task BulkInitialization_EmitsNoTvlTicks()
        {
            // The startup / full-rescan path recomputes EVERY asset; emitting there would be a tick storm on
            // every pod restart (and, with stale pair times, a source of historical-bucket pollution).
            var (repository, pool, ticks) = await CreateScenarioAsync(pairAgeDays: 400);

            await repository.InitializeFromExistingPoolsAsync(new[] { pool });

            Assert.That(ticks, Is.Empty, "bulk init must not emit TVL ticks");
        }

        private static async Task<(AggregatedPoolRepository Repository, PoolModel Pool, List<TvlTickDto> Ticks)> CreateScenarioAsync(int pairAgeDays)
        {
            var ticks = new List<TvlTickDto>();
            var hub = TvlTickCapturingHub.Create(ticks);
            var tvlRepository = new TvlOhlcRepository(null!, new LoggerFactory().CreateLogger<TvlOhlcRepository>(), hub);
            var services = new ServiceCollection().AddSingleton(tvlRepository).BuildServiceProvider();

            var assets = new MockAssetRepository();
            // The reference asset is pegged to $1 by its own recompute, which runs AFTER ALGO's in the
            // same pass - seed it like a warm pod where it is already priced.
            var usdc = (await assets.GetAssetAsync(USDC))!;
            usdc.PriceUSD = 1m;
            await assets.SetAssetAsync(usdc);

            var logger = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var repository = new AggregatedPoolRepository(null!, logger, null!, Options.Create(new AppConfiguration()), services, null, assets);

            var pool = new PoolModel
            {
                PoolAddress = "algo-usdc-pool-age-" + pairAgeDays,
                AssetIdA = ALGO,
                AssetIdB = USDC,
                AssetADecimals = 6,
                AssetBDecimals = 6,
                A = 1_000_000_000_000, // 1,000,000 ALGO
                B = 100_000_000_000,   // 100,000 USDC  => ALGO real TVL (trusted USDC side) = $100,000
                Timestamp = DateTimeOffset.UtcNow.AddDays(-pairAgeDays),
            };
            return (repository, pool, ticks);
        }
    }
}
