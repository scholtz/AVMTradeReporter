using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Tests for server-side sorting and the lightweight list payload of the pool list endpoints.
    /// Covers https://github.com/scholtz/AVMTradeReporter/issues/18
    /// </summary>
    public class PoolListSortingTests
    {
        // Unique asset ids so that the static repository caches shared between tests do not interfere.
        private const ulong BASE = 987650001UL;
        private const ulong ASSET_LOW = 987650002UL;   // TVL 100, Volume24H 900
        private const ulong ASSET_HIGH = 987650003UL;  // TVL 300, Volume24H 100
        private const ulong ASSET_MID = 987650004UL;   // TVL 200, Volume24H 500

        private static AggregatedPoolRepository CreateAggregatedPoolRepository()
        {
            var logger = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            return new AggregatedPoolRepository(null!, logger, null!, Options.Create(new AppConfiguration()), null!, null, null);
        }

        private static PoolModel CreatePool(string address, ulong assetIdA, ulong assetIdB, decimal tvlAUsd, decimal tvlBUsd, decimal volume24H, DateTimeOffset timestamp)
        {
            return new PoolModel
            {
                PoolAddress = address,
                AssetIdA = assetIdA,
                AssetADecimals = 6,
                AssetIdB = assetIdB,
                AssetBDecimals = 6,
                A = 1_000_000,
                B = 1_000_000,
                AF = 0,
                BF = 0,
                Protocol = DEXProtocol.Pact,
                Timestamp = timestamp,
                TotalTVLAssetAInUSD = tvlAUsd,
                TotalTVLAssetBInUSD = tvlBUsd,
                Volume24H = volume24H,
            };
        }

        private static async Task<AggregatedPoolRepository> CreateInitializedAggregatedRepositoryAsync()
        {
            var repository = CreateAggregatedPoolRepository();
            var now = DateTimeOffset.UtcNow;
            var pools = new List<PoolModel>
            {
                CreatePool("issue18-pool-low", BASE, ASSET_LOW, 50m, 50m, 900m, now.AddMinutes(-1)),
                CreatePool("issue18-pool-high", BASE, ASSET_HIGH, 150m, 150m, 100m, now.AddMinutes(-3)),
                CreatePool("issue18-pool-mid", BASE, ASSET_MID, 100m, 100m, 500m, now.AddMinutes(-2)),
            };
            await repository.InitializeFromExistingPoolsAsync(pools);
            // InitializeFromExistingPoolsAsync re-stores the aggregates in a fire-and-forget background
            // task that can change the cached orientation of a pair; wait for it to settle so that
            // repeated queries within a test see a stable cache.
            await Task.Delay(250);
            return repository;
        }

        private static decimal TotalTvl(AggregatedPool p) => (p.TotalTVLAssetAInUSD ?? 0) + (p.TotalTVLAssetBInUSD ?? 0);

        private static ulong OtherAsset(AggregatedPool p) => p.AssetIdA == BASE ? p.AssetIdB : p.AssetIdA;

        [Test]
        public async Task GetAllAggregatedPools_OrderByTvlDesc_ReturnsHighestTvlFirst()
        {
            var repository = await CreateInitializedAggregatedRepositoryAsync();

            var result = repository.GetAllAggregatedPools(BASE, null, 0, 100, PoolOrderBy.TVL, SortDirection.Desc).ToList();

            Assert.That(result, Is.Not.Empty);
            Assert.That(OtherAsset(result.First()), Is.EqualTo(ASSET_HIGH));
            Assert.That(TotalTvl(result.First()), Is.EqualTo(300m));
            // The whole result set must be monotonically non-increasing by TVL
            for (int i = 1; i < result.Count; i++)
            {
                Assert.That(TotalTvl(result[i]), Is.LessThanOrEqualTo(TotalTvl(result[i - 1])),
                    $"Result at index {i} is not sorted by TVL descending");
            }
            // Distinct pairs must come back in TVL order: high, mid, low
            var distinctPairs = result.Select(OtherAsset).Distinct().ToList();
            Assert.That(distinctPairs, Is.EqualTo(new[] { ASSET_HIGH, ASSET_MID, ASSET_LOW }));
        }

        [Test]
        public async Task GetAllAggregatedPools_OrderByTvlDesc_TruncationReturnsTopN()
        {
            var repository = await CreateInitializedAggregatedRepositoryAsync();

            // Regression for issue #18: with size limit, the returned slice must be the TOP items, not an arbitrary slice.
            var result = repository.GetAllAggregatedPools(BASE, null, 0, 1, PoolOrderBy.TVL, SortDirection.Desc).ToList();

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(OtherAsset(result[0]), Is.EqualTo(ASSET_HIGH));
        }

        [Test]
        public async Task GetAllAggregatedPools_OrderByTvlAsc_ReturnsLowestTvlFirst()
        {
            var repository = await CreateInitializedAggregatedRepositoryAsync();

            var result = repository.GetAllAggregatedPools(BASE, null, 0, 100, PoolOrderBy.TVL, SortDirection.Asc).ToList();

            Assert.That(result, Is.Not.Empty);
            Assert.That(OtherAsset(result.First()), Is.EqualTo(ASSET_LOW));
            var distinctPairs = result.Select(OtherAsset).Distinct().ToList();
            Assert.That(distinctPairs, Is.EqualTo(new[] { ASSET_LOW, ASSET_MID, ASSET_HIGH }));
        }

        [Test]
        public async Task GetAllAggregatedPools_OrderByVolume24HDesc_ReturnsHighestVolumeFirst()
        {
            var repository = await CreateInitializedAggregatedRepositoryAsync();

            var result = repository.GetAllAggregatedPools(BASE, null, 0, 100, PoolOrderBy.Volume24H, SortDirection.Desc).ToList();

            var distinctPairs = result.Select(OtherAsset).Distinct().ToList();
            Assert.That(distinctPairs, Is.EqualTo(new[] { ASSET_LOW, ASSET_MID, ASSET_HIGH }));
        }

        [Test]
        public async Task GetAllAggregatedPools_AssetFilterMatchesEitherSide()
        {
            var repository = await CreateInitializedAggregatedRepositoryAsync();

            // Documented behavior relied upon by biatec-scan-web: assetIdA=X and assetIdB=X return identical result sets.
            var byA = repository.GetAllAggregatedPools(BASE, null, 0, 100, PoolOrderBy.TVL, SortDirection.Desc).Select(p => p.Id).ToList();
            var byB = repository.GetAllAggregatedPools(null, BASE, 0, 100, PoolOrderBy.TVL, SortDirection.Desc).Select(p => p.Id).ToList();

            Assert.That(byA, Is.Not.Empty);
            Assert.That(byA, Is.EqualTo(byB));
        }

        [Test]
        public async Task GetAllAggregatedPools_OffsetPaginationIsStable()
        {
            var repository = await CreateInitializedAggregatedRepositoryAsync();

            var all = repository.GetAllAggregatedPools(BASE, null, 0, 100, PoolOrderBy.TVL, SortDirection.Desc).Select(p => p.Id).ToList();
            var paged = new List<string>();
            for (int offset = 0; offset < all.Count; offset += 2)
            {
                paged.AddRange(repository.GetAllAggregatedPools(BASE, null, offset, 2, PoolOrderBy.TVL, SortDirection.Desc).Select(p => p.Id));
            }

            Assert.That(paged, Is.EqualTo(all));
        }

        [Test]
        public void ToLightweight_OmitsPoolListsFromJson_ButKeepsOtherFields()
        {
            var pool = AggregatedPool.FromPools(new List<PoolModel>
            {
                CreatePool("issue18-light-pool", 987651001UL, 987651002UL, 10m, 20m, 5m, DateTimeOffset.UtcNow)
            }).First();

            Assert.That(pool.Level1Pools, Is.Not.Null.And.Not.Empty);

            var light = pool.ToLightweight();

            Assert.That(light.Level1Pools, Is.Null);
            Assert.That(light.Level2Pools, Is.Null);
            Assert.That(light.AssetIdA, Is.EqualTo(pool.AssetIdA));
            Assert.That(light.AssetIdB, Is.EqualTo(pool.AssetIdB));
            Assert.That(light.PoolCount, Is.EqualTo(pool.PoolCount));
            Assert.That(TotalTvl(light), Is.EqualTo(TotalTvl(pool)));

            // System.Text.Json (used by ASP.NET Core) must omit the collections entirely in light mode
            var lightJson = JsonSerializer.Serialize(light);
            Assert.That(lightJson, Does.Not.Contain("Level1Pools"));
            Assert.That(lightJson, Does.Not.Contain("Level2Pools"));

            var fullJson = JsonSerializer.Serialize(pool);
            Assert.That(fullJson, Does.Contain("Level1Pools"));

            // The original instance must not be mutated by creating the lightweight copy
            Assert.That(pool.Level1Pools, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public async Task GetPoolsAsync_OrderByTvl_SortsPools()
        {
            var loggerPool = new LoggerFactory().CreateLogger<PoolRepository>();
            var aggregatedRepository = CreateAggregatedPoolRepository();
            var poolRepository = new PoolRepository(null!, loggerPool, null!, aggregatedRepository, new OptionsWrapper<AppConfiguration>(new AppConfiguration()), null!, null, null);
            var ct = CancellationToken.None;

            const ulong P_BASE = 987652001UL;
            var now = DateTimeOffset.UtcNow;
            var poolLow = CreatePool("issue18-p-low", P_BASE, 987652002UL, 25m, 25m, 0m, now.AddMinutes(-1));
            var poolHigh = CreatePool("issue18-p-high", P_BASE, 987652003UL, 250m, 250m, 0m, now.AddMinutes(-3));
            var poolMid = CreatePool("issue18-p-mid", P_BASE, 987652004UL, 100m, 100m, 0m, now.AddMinutes(-2));
            await poolRepository.StorePoolAsync(poolLow, false, ct);
            await poolRepository.StorePoolAsync(poolHigh, false, ct);
            await poolRepository.StorePoolAsync(poolMid, false, ct);

            var byTvlDesc = await poolRepository.GetPoolsAsync(P_BASE, null, null, null, 100, PoolOrderBy.TVL, SortDirection.Desc, ct);
            Assert.That(byTvlDesc.Select(p => p.PoolAddress), Is.EqualTo(new[] { "issue18-p-high", "issue18-p-mid", "issue18-p-low" }));

            var byTvlAsc = await poolRepository.GetPoolsAsync(P_BASE, null, null, null, 100, PoolOrderBy.TVL, SortDirection.Asc, ct);
            Assert.That(byTvlAsc.Select(p => p.PoolAddress), Is.EqualTo(new[] { "issue18-p-low", "issue18-p-mid", "issue18-p-high" }));

            // Top-1 by TVL must be the highest TVL pool, not an arbitrary one
            var top1 = await poolRepository.GetPoolsAsync(P_BASE, null, null, null, 1, PoolOrderBy.TVL, SortDirection.Desc, ct);
            Assert.That(top1.Single().PoolAddress, Is.EqualTo("issue18-p-high"));

            // Default (no orderBy) keeps the historical behavior: newest first
            var defaultOrder = await poolRepository.GetPoolsAsync(P_BASE, null, null, null, 100, cancellationToken: ct);
            Assert.That(defaultOrder.Select(p => p.PoolAddress), Is.EqualTo(new[] { "issue18-p-low", "issue18-p-mid", "issue18-p-high" }));
        }
    }
}
