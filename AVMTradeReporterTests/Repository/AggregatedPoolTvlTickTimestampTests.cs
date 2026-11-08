using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.DTO.TVL;
using AVMTradeReporter.Repository;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
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
        private const ulong USDC = 31566704UL; // AppConfiguration default UsdReferenceAssetId

        [Test]
        public async Task StalePairUpdate_StampsTvlTickWithComputeTime_NotThePairsLastTradeTime()
        {
            var ticks = new List<TvlTickDto>();
            var hub = CreateCapturingHub(ticks);
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

            // ALGO/USDC pool that last traded 400 days ago - exactly what a full volume sweep re-stores.
            var staleTradeTime = DateTimeOffset.UtcNow.AddDays(-400);
            var pool = new PoolModel
            {
                PoolAddress = "stale-algo-usdc-pool",
                AssetIdA = ALGO,
                AssetIdB = USDC,
                AssetADecimals = 6,
                AssetBDecimals = 6,
                A = 1_000_000_000_000, // 1,000,000 ALGO
                B = 100_000_000_000,   // 100,000 USDC  => ALGO real TVL (trusted USDC side) = $100,000
                Timestamp = staleTradeTime,
            };

            var before = DateTimeOffset.UtcNow;
            await repository.UpdateForPairAsync(ALGO, USDC, new[] { pool });
            var after = DateTimeOffset.UtcNow;

            var algoTicks = ticks.Where(t => t.AssetId == ALGO).ToList();
            Assert.That(algoTicks, Is.Not.Empty, "the recompute must emit an ALGO TVL tick for this test to be meaningful");
            foreach (var tick in algoTicks)
            {
                Assert.That(tick.Timestamp, Is.InRange(before.ToUnixTimeSeconds() - 1, after.ToUnixTimeSeconds() + 1),
                    "A TVL tick is the TVL as of the moment it was computed. Stamping it with the pair's last-trade time " +
                    $"({staleTradeTime:O}) writes today's TVL into a historical candle and makes the 7d TVL chart stripy.");
            }
        }

        private static IHubContext<BiatecScanHub> CreateCapturingHub(List<TvlTickDto> sink)
        {
            var proxy = new Mock<IClientProxy>();
            proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Callback<string, object?[], CancellationToken>((_, args, _) =>
                {
                    // IClientProxy delivers its payload as object?[]; the TVL stream always carries one TvlTickDto.
                    if (args.Length == 1 && args[0] is TvlTickDto tick) lock (sink) sink.Add(tick);
                })
                .Returns(Task.CompletedTask);
            var clients = new Mock<IHubClients>();
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
            var hub = new Mock<IHubContext<BiatecScanHub>>();
            hub.SetupGet(h => h.Clients).Returns(clients.Object);
            return hub.Object;
        }
    }
}
