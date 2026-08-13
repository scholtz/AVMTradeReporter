using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Regression coverage for the 2026-08-13 production incident: promoting to Algorand mainnet
    /// failed with "deployment ... exceeded its progress deadline" because the new pod never finished
    /// starting. Root cause: InitializeFromExistingPoolsAsync (awaited synchronously from Program.cs
    /// before Kestrel opens the port, per the earlier HA-preload fix) called UpdateRelatedAssetsAsync
    /// once per pool, and that method's final step rescanned the *entire* aggregated-pool cache
    /// (every pool, not just the ones related to the one just stored) to refresh denormalized
    /// historical-price fields. For N pools that is an O(N) rescan repeated N times - O(N^2) - which
    /// was invisible before (the whole thing ran fire-and-forget in the background) but became a
    /// startup-blocking bottleneck on production's real pool count once it was made synchronous.
    /// Fixed by doing the full-cache rescan exactly once per bulk load (RefreshAllAssetStatsAsync)
    /// instead of once per pool.
    /// </summary>
    public class AggregatedPoolBulkInitPerformanceTests
    {
        private class CountingAssetRepository : IAssetRepository
        {
            private readonly MockAssetRepository _inner = new();
            public int GetAssetCalls;

            public Task<BiatecAsset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default)
            {
                GetAssetCalls++;
                return _inner.GetAssetAsync(assetId, cancellationToken);
            }

            public Task SetAssetAsync(BiatecAsset asset, CancellationToken cancellationToken = default) =>
                _inner.SetAssetAsync(asset, cancellationToken);

            public Task<IEnumerable<BiatecAsset>> GetAssetsAsync(IEnumerable<ulong>? ids, string? search, int offset, int size, CancellationToken cancellationToken) =>
                _inner.GetAssetsAsync(ids, search, offset, size, cancellationToken);
        }

        [Test]
        public async Task InitializeFromExistingPools_AssetLookupsScaleLinearly_NotQuadratically()
        {
            // The aggregated-pool cache is a static field shared across every test in this process
            // (deliberately, so it survives DI scope disposal in production) - reset it first so
            // other tests' pools don't inflate the call count this assertion depends on.
            AggregatedPoolRepository.ResetForTests();

            const int poolCount = 40;
            var assetRepository = new CountingAssetRepository();
            var logger = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var repository = new AggregatedPoolRepository(null!, logger, null!, Options.Create(new AppConfiguration()), null!, null, assetRepository);

            // Star topology: pool i pairs asset (1000+i) against ALGO - the worst case that used to
            // trigger the O(pools^2) full-cache rescan on every single one of the pool-store calls.
            var pools = Enumerable.Range(1, poolCount)
                .Select(i => new PoolModel
                {
                    PoolAddress = $"pool-{i}",
                    AssetIdA = (ulong)(1000 + i),
                    AssetIdB = 0UL,
                    AssetADecimals = 6,
                    AssetBDecimals = 6,
                    A = 1_000_000,
                    B = 1_000_000,
                })
                .ToList();

            await repository.InitializeFromExistingPoolsAsync(pools);

            // O(N^2) for N=40 would be in the low thousands (the old code's final rescan alone was
            // ~2*N per call, N times, before even counting the per-asset TVL/price lookups). A linear
            // pass touches roughly one GetAssetAsync per distinct asset for the bulk recompute plus
            // two per cached pool (both orientations) for the one-time historical-price refresh -
            // comfortably under 10x the pool count.
            Assert.That(assetRepository.GetAssetCalls, Is.LessThan(poolCount * 10),
                $"GetAssetAsync was called {assetRepository.GetAssetCalls} times for {poolCount} pools - " +
                "this looks quadratic again (see AggregatedPoolRepository.RefreshAllAssetStatsAsync).");
        }
    }
}
