using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Services;
using NUnit.Framework;

namespace AVMTradeReporterTests.Services
{
    /// <summary>
    /// Unit tests for the pure selection logic of <see cref="TopAssetsService.Build"/> — the six
    /// "top assets" highlight lists (Popular, Trending, Top gainers/losers, Top value gainers/losers).
    /// </summary>
    [TestFixture]
    public class TopAssetsServiceTests
    {
        private static BiatecAsset MakeAsset(
            ulong id,
            string name,
            decimal tvl,
            decimal? volume1h = null,
            decimal? volume24h = null,
            decimal price = 1m,
            decimal? price24h = null,
            int stabilityIndex = 0)
        {
            return new BiatecAsset
            {
                Index = id,
                Params = new Algorand.Algod.Model.AssetParams { Name = name, UnitName = name, Decimals = 6 },
                TVL_USD = tvl,
                Volume1H = volume1h,
                Volume24H = volume24h,
                PriceUSD = price,
                PriceUSD24H = price24h,
                StabilityIndex = stabilityIndex
            };
        }

        [Test]
        public void Build_ExcludesStableAssetsFromEveryList()
        {
            var assets = new[]
            {
                MakeAsset(1, "USDC", tvl: 1000000, volume1h: 500, volume24h: 9000, price: 1m, price24h: 0.9m, stabilityIndex: 100),
                MakeAsset(2, "VOTE", tvl: 500, volume1h: 100, volume24h: 200, price: 2m, price24h: 1m),
            };

            var result = TopAssetsService.Build(assets, null, null, 3, DateTimeOffset.UtcNow);

            Assert.That(result.Popular.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 2 }));
            Assert.That(result.Trending.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 2 }));
            Assert.That(result.TopGainers.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 2 }));
        }

        [Test]
        public void Build_PopularSortedByVolume24h_TrendingByVolume1h_LimitedToListSize()
        {
            var assets = new[]
            {
                MakeAsset(1, "A", tvl: 900, volume1h: 1, volume24h: 100),
                MakeAsset(2, "B", tvl: 800, volume1h: 2, volume24h: 400),
                MakeAsset(3, "C", tvl: 700, volume1h: 3, volume24h: 300),
                MakeAsset(4, "D", tvl: 600, volume1h: 4, volume24h: 200),
            };

            var result = TopAssetsService.Build(assets, null, null, 3, DateTimeOffset.UtcNow);

            Assert.That(result.Popular.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 2, 3, 4 }));
            Assert.That(result.Trending.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 4, 3, 2 }));
        }

        [Test]
        public void Build_GainersAndLosersSplitByPriceChangeSign()
        {
            var assets = new[]
            {
                MakeAsset(1, "UP2", tvl: 900, price: 1.2m, price24h: 1m),   // +20%
                MakeAsset(2, "UP1", tvl: 800, price: 1.1m, price24h: 1m),   // +10%
                MakeAsset(3, "DOWN", tvl: 700, price: 0.5m, price24h: 1m),  // -50%
                MakeAsset(4, "FLAT", tvl: 600, price: 1m, price24h: 1m),    // 0%
                MakeAsset(5, "NOHIST", tvl: 500, price: 1m, price24h: null),
            };

            var result = TopAssetsService.Build(assets, null, null, 3, DateTimeOffset.UtcNow);

            Assert.That(result.TopGainers.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 1, 2 }));
            Assert.That(result.TopLosers.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 3 }));
            Assert.That(result.TopGainers[0].PriceChange24HPercent, Is.EqualTo(20m).Within(0.0001m));
            Assert.That(result.TopLosers[0].PriceChange24HPercent, Is.EqualTo(-50m).Within(0.0001m));
        }

        [Test]
        public void Build_ValueGainersAndLosersUseTvlSnapshotDiff()
        {
            var assets = new[]
            {
                MakeAsset(1, "GROW", tvl: 2000),
                MakeAsset(2, "SHRINK", tvl: 400),
                MakeAsset(3, "NEW", tvl: 300), // no snapshot entry -> excluded from value lists
            };
            var snapshot = new Dictionary<ulong, decimal> { { 1, 1000m }, { 2, 900m } };

            var result = TopAssetsService.Build(assets, snapshot, null, 3, DateTimeOffset.UtcNow);

            Assert.That(result.TopValueGainers.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 1 }));
            Assert.That(result.TopValueLosers.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 2 }));
            Assert.That(result.TopValueGainers[0].TVLChange24HUSD, Is.EqualTo(1000m));
            Assert.That(result.TopValueGainers[0].TVLChange24HPercent, Is.EqualTo(100m).Within(0.0001m));
            Assert.That(result.TopValueLosers[0].TVLChange24HUSD, Is.EqualTo(-500m));
            Assert.That(result.TopValueGainers[0].RealTVLUSD24H, Is.EqualTo(1000m));
        }

        [Test]
        public void Build_ValueListsRankByPercentChangeNotAbsoluteUsd()
        {
            var assets = new[]
            {
                // +$10000 but only +10% vs +$500 but +50% — percent ranking must win.
                MakeAsset(1, "BIGUSD", tvl: 110000),
                MakeAsset(2, "BIGPCT", tvl: 1500),
                // -$20000 but only -20% vs -$400 but -40%.
                MakeAsset(3, "BIGUSDLOSS", tvl: 80000),
                MakeAsset(4, "BIGPCTLOSS", tvl: 600),
            };
            var snapshot = new Dictionary<ulong, decimal>
            {
                { 1, 100000m }, { 2, 1000m }, { 3, 100000m }, { 4, 1000m }
            };

            var result = TopAssetsService.Build(assets, snapshot, null, 3, DateTimeOffset.UtcNow);

            Assert.That(result.TopValueGainers.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 2, 1 }));
            Assert.That(result.TopValueLosers.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 4, 3 }));
        }

        [Test]
        public void Build_WithoutSnapshot_ValueListsAreEmpty()
        {
            var assets = new[] { MakeAsset(1, "A", tvl: 1000, volume24h: 10) };

            var result = TopAssetsService.Build(assets, null, null, 3, DateTimeOffset.UtcNow);

            Assert.That(result.TopValueGainers, Is.Empty);
            Assert.That(result.TopValueLosers, Is.Empty);
            Assert.That(result.Popular, Has.Count.EqualTo(1));
        }

        [Test]
        public void Build_WindowedVolumesOverrideStaleAssetCounters()
        {
            // Asset 1 carries a stale non-zero Volume1H counter but had no trades in the past
            // 48h (absent from the windows dictionary) -> must not appear in trending/popular.
            var assets = new[]
            {
                MakeAsset(1, "STALE", tvl: 900, volume1h: 500, volume24h: 500),
                MakeAsset(2, "LIVE", tvl: 800, volume1h: 0, volume24h: 0),
            };
            var volumes = new Dictionary<ulong, AssetVolumeWindows>
            {
                { 2, new AssetVolumeWindows(Volume1H: 50m, Volume1HPrev: 25m, Volume24H: 300m, Volume24HPrev: 600m) },
            };

            var result = TopAssetsService.Build(assets, null, volumes, 3, DateTimeOffset.UtcNow);

            Assert.That(result.Trending.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 2 }));
            Assert.That(result.Popular.Select(i => i.AssetId), Is.EqualTo(new ulong[] { 2 }));
            var item = result.Trending[0];
            Assert.That(item.Volume1HUSD, Is.EqualTo(50m));
            Assert.That(item.Volume1HUSDPrev, Is.EqualTo(25m));
            Assert.That(item.Volume1HChangePercent, Is.EqualTo(100m).Within(0.0001m));
            Assert.That(item.Volume24HUSD, Is.EqualTo(300m));
            Assert.That(item.Volume24HUSDPrev, Is.EqualTo(600m));
            Assert.That(item.Volume24HChangePercent, Is.EqualTo(-50m).Within(0.0001m));
        }

        [Test]
        public void Build_WindowedVolumes_NoPreviousVolume_PercentIsNull()
        {
            var assets = new[] { MakeAsset(1, "A", tvl: 900) };
            var volumes = new Dictionary<ulong, AssetVolumeWindows>
            {
                { 1, new AssetVolumeWindows(Volume1H: 10m, Volume1HPrev: 0m, Volume24H: 10m, Volume24HPrev: 0m) },
            };

            var result = TopAssetsService.Build(assets, null, volumes, 3, DateTimeOffset.UtcNow);

            Assert.That(result.Trending[0].Volume1HChangePercent, Is.Null);
            Assert.That(result.Popular[0].Volume24HChangePercent, Is.Null);
        }
    }
}
