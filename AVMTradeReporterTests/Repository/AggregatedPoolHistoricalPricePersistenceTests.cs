using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.DTO.OHLC;
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
    /// Regression coverage for "Top Losers/Gainers stuck at a stale percentage for days" (2026-08-19).
    /// Root cause: AggregatedPoolRepository.RecomputeSingleAssetAsync assigned the freshly-fetched
    /// PriceUSD1H/24H/7D onto the in-memory BiatecAsset unconditionally, but only flipped the
    /// `changed` flag that gates SetAssetAsync (the only thing that persists to Redis/Elasticsearch
    /// and broadcasts over SignalR) when PriceUSD/TVL/PoolsCount happened to also change in that
    /// same call. Whenever a recompute moved only the 24h baseline (which slides on essentially
    /// every call since it targets a rolling "24h ago" instant) while current price/TVL/pool count
    /// were unchanged, the new baseline was applied to the object but never persisted - invisible
    /// within one long-lived process (GetAssetAsync returns the same mutated reference), but exposed
    /// as a frozen PriceUSD24H (and thus a frozen priceChange24HPercent on the Top Losers/Gainers
    /// cards) as soon as the process cache was rehydrated from the durable store, e.g. after a pod
    /// restart/rolling deploy.
    /// </summary>
    public class AggregatedPoolHistoricalPricePersistenceTests
    {
        private const ulong ASSET = 555000101UL;
        private const ulong ALGO = 0UL;

        /// <summary>Minimal IOHLCService stub - only GetHistoricalPriceAsync is exercised by
        /// RecomputeSingleAssetAsync; every other member is unused by this code path.</summary>
        private class FakeOhlcService : IOHLCService
        {
            public decimal? PriceUSD1H;
            public decimal? PriceUSD24H;
            public decimal? PriceUSD7D;

            public Task<decimal?> GetHistoricalPriceAsync(ulong assetId, TimeSpan ago, CancellationToken ct)
            {
                if (ago == TimeSpan.FromHours(1)) return Task.FromResult(PriceUSD1H);
                if (ago == TimeSpan.FromHours(24)) return Task.FromResult(PriceUSD24H);
                return Task.FromResult(PriceUSD7D);
            }

            public object GetConfig() => throw new NotImplementedException();
            public long GetTime() => throw new NotImplementedException();
            public Task<object?> GetSymbolAsync(string symbol, CancellationToken ct) => throw new NotImplementedException();
            public Task<IEnumerable<object>> SearchAsync(string query, int limit, CancellationToken ct) => throw new NotImplementedException();
            public object GetMarks() => throw new NotImplementedException();
            public object GetTimescaleMarks() => throw new NotImplementedException();
            public object GetQuotes(string symbols) => throw new NotImplementedException();
            public Task<object> GetHistoryAsync(ulong assetA, ulong assetB, string resolution, long from, long to, CancellationToken ct) => throw new NotImplementedException();
            public Task<SymbolInfoDto> GetSymbolInfoAsync(string symbols, CancellationToken ct) => throw new NotImplementedException();
        }

        [Test]
        public async Task RecomputeSingleAsset_PersistsAsset_WhenOnlyHistoricalBaselineMoves()
        {
            AggregatedPoolRepository.ResetForTests();

            var assetRepository = new MockAssetRepository();
            var asset = (await assetRepository.GetAssetAsync(ASSET))!;
            // Current price/TVL/pool-count are already at their steady-state values, so this
            // recompute call will not touch any of them - only the 24h baseline moves.
            asset.PriceUSD = 2.0m;
            asset.PriceUSD1H = 2.0m;
            asset.PriceUSD24H = 3.6m; // stale baseline from a week-old -45% drop
            asset.PriceUSD7D = 2.0m;
            await assetRepository.SetAssetAsync(asset);
            assetRepository.SetAssetCallCount = 0; // ignore the setup call above

            var fakeOhlc = new FakeOhlcService
            {
                PriceUSD1H = 2.0m,
                PriceUSD24H = 2.05m, // the true, moved 24h-ago price
                PriceUSD7D = 2.0m,
            };
            var services = new ServiceCollection();
            services.AddSingleton<IOHLCService>(fakeOhlc);
            var serviceProvider = services.BuildServiceProvider();

            var logger = new LoggerFactory().CreateLogger<AggregatedPoolRepository>();
            var repository = new AggregatedPoolRepository(
                null!, logger, null!, Options.Create(new AppConfiguration()), serviceProvider, null, assetRepository);

            var pool = new PoolModel
            {
                PoolAddress = "algo-pool",
                AssetIdA = ASSET,
                AssetIdB = ALGO,
                AssetADecimals = 6,
                AssetBDecimals = 6,
                A = 1_000_000,
                B = 1_000_000,
                Timestamp = DateTimeOffset.UtcNow,
            };
            await repository.InitializeFromExistingPoolsAsync(new[] { pool });

            await repository.UpdateForPairAsync(ASSET, ALGO, new[] { pool });

            Assert.That(assetRepository.SetAssetCallCount, Is.GreaterThan(0),
                "A PriceUSD24H-only change must still trigger SetAssetAsync, otherwise the moved " +
                "baseline is applied to the in-memory asset but never persisted to Redis/" +
                "Elasticsearch/SignalR - it silently reverts to the stale value on the next cache " +
                "rehydration (e.g. a pod restart), freezing the Top Losers/Gainers percentage.");

            var persisted = await assetRepository.GetAssetAsync(ASSET);
            Assert.That(persisted!.PriceUSD24H, Is.EqualTo(2.05m));
        }
    }
}
