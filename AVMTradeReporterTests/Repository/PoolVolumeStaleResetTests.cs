using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Regression coverage for the pool-details page showing a frozen, non-zero 24H volume for a
    /// pool that has not traded in 11+ days. Root cause: <see cref="TradeQueryService.GetPoolVolumesAsync"/>
    /// builds its result purely from Elasticsearch terms-aggregation buckets - a pool with zero
    /// matching trades in a given window produces no bucket at all, so it is silently absent from
    /// the returned dictionary for that window. <see cref="PoolRepository.UpdatePoolVolumesAsync"/>
    /// then iterated only the RETURNED dictionary's keys, so a pool absent from every window
    /// (because it has gone fully quiet, even beyond the widest 7D lookback) was never visited and
    /// its last-known, stale Volume1H/24H/7D were left untouched forever - even when that exact pool
    /// address was included in the sweep's requested pool list.
    /// </summary>
    public class PoolVolumeStaleResetTests
    {
        private class FakeTradeQueryService : ITradeQueryService
        {
            private readonly Dictionary<string, (decimal Volume1H, decimal Volume24H, decimal Volume7D)>? _volumes;

            public FakeTradeQueryService(Dictionary<string, (decimal, decimal, decimal)>? volumes)
            {
                _volumes = volumes;
            }

            public Task<IEnumerable<Trade>> GetTradesAsync(
                ulong? assetIdIn = null,
                ulong? assetIdOut = null,
                string? txId = null,
                int offset = 0,
                int size = 100,
                CancellationToken cancellationToken = default)
                => Task.FromResult<IEnumerable<Trade>>(new List<Trade>());

            public Task<PagedResult<Trade>> GetTradesAsync(TradeFilter filter, CancellationToken cancellationToken = default)
                => Task.FromResult(new PagedResult<Trade>
                {
                    Items = new List<Trade>(),
                    Total = 0,
                    Offset = filter.Offset,
                    Size = filter.Size,
                    HasMore = false
                });

            public Task<Dictionary<string, (decimal Volume1H, decimal Volume24H, decimal Volume7D)>?> GetPoolVolumesAsync(
                IEnumerable<string> poolAddresses, CancellationToken cancellationToken = default)
                => Task.FromResult(_volumes == null ? null : new Dictionary<string, (decimal, decimal, decimal)>(_volumes));

            public Task<IReadOnlyDictionary<ulong, AssetVolumeWindows>?> GetAssetVolumeWindowsAsync(
                DateTimeOffset now, CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyDictionary<ulong, AssetVolumeWindows>?>(null);
        }

        private static PoolRepository CreateRepository(ITradeQueryService tradeQueryService)
        {
            var logger = new LoggerFactory().CreateLogger<PoolRepository>();
            var aggregatedLogger = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var aggregated = new AggregatedPoolRepository(null!, aggregatedLogger, null!, Options.Create(new AppConfiguration()), null!, null, null);

            var services = new ServiceCollection();
            services.AddSingleton(tradeQueryService);
            var serviceProvider = services.BuildServiceProvider();

            return new PoolRepository(null!, logger, null!, aggregated, Options.Create(new AppConfiguration()), serviceProvider, null, null);
        }

        [Test]
        public async Task UpdatePoolVolumesAsync_ResetsQuietPool_ToZero_WhenNoTradesInAnyWindow()
        {
            // "quiet-pool" last traded 11 days ago - beyond even the widest 7D window - so
            // GetPoolVolumesAsync's ES aggregation emits no bucket, and thus no dictionary entry,
            // for it at all in any of the three periods.
            var fakeTradeQueryService = new FakeTradeQueryService(new Dictionary<string, (decimal, decimal, decimal)>());
            var repository = CreateRepository(fakeTradeQueryService);
            var ct = CancellationToken.None;

            var quietPool = new PoolModel
            {
                PoolAddress = "quiet-pool",
                AssetIdA = 1,
                AssetIdB = 2,
                AssetADecimals = 6,
                AssetBDecimals = 6,
                Volume1H = 12m,
                Volume24H = 675.08m, // stale/frozen from when it was actively trading
                Volume7D = 900m,
                Timestamp = DateTimeOffset.UtcNow.AddDays(-11),
            };
            await repository.StorePoolAsync(quietPool, false, ct);

            await repository.UpdatePoolVolumesAsync(new[] { "quiet-pool" }, ct);

            var updated = await repository.GetPoolAsync("quiet-pool", ct);
            Assert.That(updated!.Volume24H, Is.EqualTo(0m),
                "A pool absent from every GetPoolVolumesAsync window (no trades at all in the last " +
                "7D) must have its Volume24H reset to 0, not left at its last-known stale value.");
            Assert.That(updated.Volume1H, Is.EqualTo(0m));
            Assert.That(updated.Volume7D, Is.EqualTo(0m));
        }

        [Test]
        public async Task UpdatePoolVolumesAsync_UpdatesActivePool_ToRealWindowedValue()
        {
            var fakeTradeQueryService = new FakeTradeQueryService(new Dictionary<string, (decimal, decimal, decimal)>
            {
                ["active-pool"] = (5m, 50m, 500m)
            });
            var repository = CreateRepository(fakeTradeQueryService);
            var ct = CancellationToken.None;

            var activePool = new PoolModel
            {
                PoolAddress = "active-pool",
                AssetIdA = 1,
                AssetIdB = 2,
                AssetADecimals = 6,
                AssetBDecimals = 6,
                Volume1H = 1m,
                Volume24H = 1m,
                Volume7D = 1m,
                Timestamp = DateTimeOffset.UtcNow,
            };
            await repository.StorePoolAsync(activePool, false, ct);

            await repository.UpdatePoolVolumesAsync(new[] { "active-pool" }, ct);

            var updated = await repository.GetPoolAsync("active-pool", ct);
            Assert.That(updated!.Volume1H, Is.EqualTo(5m));
            Assert.That(updated.Volume24H, Is.EqualTo(50m));
            Assert.That(updated.Volume7D, Is.EqualTo(500m));
        }

        [Test]
        public async Task UpdatePoolVolumesAsync_LeavesLastKnownVolume_WhenTradeQueryServiceReportsFailure()
        {
            // GetPoolVolumesAsync returns null when Elasticsearch is unavailable or a window's query
            // failed (see TradeQueryService). A transient outage must never be mistaken for "these
            // pools genuinely have zero volume" - that would wipe every pool's real, last-known
            // volume to 0 on every ES hiccup, which is worse than the stale-value bug this fixes.
            var fakeTradeQueryService = new FakeTradeQueryService(null);
            var repository = CreateRepository(fakeTradeQueryService);
            var ct = CancellationToken.None;

            var pool = new PoolModel
            {
                PoolAddress = "pool-during-outage",
                AssetIdA = 1,
                AssetIdB = 2,
                AssetADecimals = 6,
                AssetBDecimals = 6,
                Volume1H = 3m,
                Volume24H = 42m,
                Volume7D = 500m,
                Timestamp = DateTimeOffset.UtcNow,
            };
            await repository.StorePoolAsync(pool, false, ct);

            await repository.UpdatePoolVolumesAsync(new[] { "pool-during-outage" }, ct);

            var updated = await repository.GetPoolAsync("pool-during-outage", ct);
            Assert.That(updated!.Volume24H, Is.EqualTo(42m),
                "A failed volume query must leave the pool's last-known volume untouched, not zero it.");
            Assert.That(updated.Volume1H, Is.EqualTo(3m));
            Assert.That(updated.Volume7D, Is.EqualTo(500m));
        }
    }
}
