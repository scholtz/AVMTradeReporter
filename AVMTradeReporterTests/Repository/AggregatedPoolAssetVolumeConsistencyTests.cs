using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Regression coverage for the "Popular assets" cards vs. the Assets table's 24h-volume sort
    /// disagreeing about which assets rank highest, and displaying different volume figures for the
    /// same asset. Root cause: BiatecAsset.Volume24H had two independent writers -
    /// TopAssetsService.SyncAssetVolumeCountersAsync (every ~5 min, queries trades directly per
    /// asset via ITradeQueryService - decays correctly as old trades roll out of the 24h window)
    /// and AggregatedPoolRepository.UpdateRelatedAssetsAsync (on every single trade anywhere in the
    /// system, summing every cached AggregatedPool.Volume24H that touches the asset). The second
    /// writer's inputs are pool-level Volume24H snapshots that only refresh when
    /// VolumeUpdateBackgroundService sees *new* trades on that specific pool - a pool that stops
    /// trading keeps a frozen, non-decaying Volume24H forever. Whenever any other pool for the same
    /// asset got a fresh trade, the live path resummed and overwrote the just-synced, accurate
    /// asset.Volume24H with a total inflated by that frozen figure - which is exactly why a
    /// low-current-volume asset (frozen high historical Volume24H on a now-quiet pool) could rank
    /// above the real top volume assets in the table while never appearing in "Popular" (which reads
    /// the honest per-asset windows directly, never the drifted BiatecAsset.Volume24H field).
    /// </summary>
    public class AggregatedPoolAssetVolumeConsistencyTests
    {
        private const ulong ASSET = 555000001UL;
        private const ulong ALGO = 0UL;
        private const ulong USDC = 555000002UL;

        private static AggregatedPoolRepository CreateRepository(MockAssetRepository assetRepository)
        {
            var logger = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            return new AggregatedPoolRepository(null!, logger, null!, Options.Create(new AppConfiguration()), null!, null, assetRepository);
        }

        [Test]
        public async Task LiveUpdate_DoesNotOverwrite_AlreadySyncedAssetVolume_WithStalePoolSum()
        {
            // Arrange: TopAssetsService already ran its accurate, ES-backed sync and set the asset's
            // honest trailing 24h volume to 120 (e.g. its only currently-active pool, ASSET/ALGO,
            // genuinely traded $120 in the last 24h).
            var assetRepository = new MockAssetRepository();
            var asset = (await assetRepository.GetAssetAsync(ASSET))!;
            asset.Volume24H = 120m;
            await assetRepository.SetAssetAsync(asset);

            var repository = CreateRepository(assetRepository);

            // ASSET/USDC stopped trading hours ago; VolumeUpdateBackgroundService only refreshes
            // pools with brand new trades, so this pool's cached Volume24H is a frozen, stale
            // snapshot from when it was still actively trading - it does not decay just because time
            // has passed without a matching real trade.
            var stalePool = new PoolModel
            {
                PoolAddress = "stale-usdc-pool",
                AssetIdA = ASSET,
                AssetIdB = USDC,
                AssetADecimals = 6,
                AssetBDecimals = 6,
                A = 1_000_000,
                B = 1_000_000,
                Volume24H = 5000m, // stale/frozen
                Timestamp = DateTimeOffset.UtcNow.AddHours(-6),
            };

            // ASSET/ALGO just traded - this is the pool whose fresh update triggers the recompute.
            var freshPool = new PoolModel
            {
                PoolAddress = "fresh-algo-pool",
                AssetIdA = ASSET,
                AssetIdB = ALGO,
                AssetADecimals = 6,
                AssetBDecimals = 6,
                A = 1_000_000,
                B = 1_000_000,
                Volume24H = 100m, // matches the honest total already synced onto the asset
                Timestamp = DateTimeOffset.UtcNow,
            };

            await repository.InitializeFromExistingPoolsAsync(new[] { stalePool, freshPool });

            // Act: a brand new trade lands on the fresh pool, the normal live-update path.
            await repository.UpdateForPairAsync(ASSET, ALGO, new[] { freshPool, stalePool });

            // Assert: the asset's Volume24H must still reflect the honest, already-synced figure
            // TopAssetsService computed - not a resummed total inflated by the stale USDC pool.
            var updated = await assetRepository.GetAssetAsync(ASSET);
            Assert.That(updated!.Volume24H, Is.EqualTo(120m),
                "AggregatedPoolRepository must not recompute/overwrite asset.Volume24H from the " +
                "pool cache - TopAssetsService's ITradeQueryService-backed sync is the single " +
                "source of truth so the Assets table and the Popular/Trending cards never disagree.");
        }
    }
}
