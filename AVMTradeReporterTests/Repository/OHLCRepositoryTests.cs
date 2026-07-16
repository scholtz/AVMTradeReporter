using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AVMTradeReporterTests.Repository
{
    public class OHLCRepositoryTests
    {
        private OHLCRepository _repo = new(null, null);

        [Test]
        public async Task GetIntervalBuckets_BasicTrade_GeneratesAllBuckets()
        {
            var trade = new Trade
            {
                AssetIdIn = 1,
                AssetIdOut = 2,
                AssetAmountIn = 100,
                AssetAmountOut = 250,
                ValueUSD = 1000m,
                Timestamp = DateTimeOffset.Parse("2024-01-02T03:04:05Z"),
                TradeState = AVMTradeReporter.Models.Data.Enums.TxState.Confirmed
            };

            var buckets = await _repo.GetIntervalBuckets(trade);
            // 1 asset-valuation bucket + 2 usd-valuation buckets (one per asset side, each priced against USDC) per interval
            Assert.That(buckets.Count, Is.EqualTo(OHLCRepository.Intervals.Length * 3));

            var assetSeries = buckets.Where(b => b.InUsdValuation == false).ToList();
            var usdSeries = buckets.Where(b => b.InUsdValuation == true).ToList();

            Assert.That(assetSeries.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(usdSeries.Count, Is.EqualTo(OHLCRepository.Intervals.Length * 2));

            foreach (var b in assetSeries)
            {
                Assert.That(b.Price, Is.EqualTo(2.5m));
                Assert.That(b.VolumeBase, Is.EqualTo(100m));
                Assert.That(b.VolumeQuote, Is.EqualTo(250m));
                Assert.That(OHLCRepository.Intervals.Select(i => i.code), Does.Contain(b.Interval));
                Assert.That(b.DocId, Does.StartWith($"1-2-{b.Interval}-asset-"));
            }

            const ulong usdcAssetId = 31566704UL;
            var usdSeriesForAssetA = usdSeries.Where(b => b.AssetIdA == 1UL).ToList();
            var usdSeriesForAssetB = usdSeries.Where(b => b.AssetIdA == 2UL).ToList();

            Assert.That(usdSeriesForAssetA.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(usdSeriesForAssetB.Count, Is.EqualTo(OHLCRepository.Intervals.Length));

            foreach (var b in usdSeriesForAssetA)
            {
                // asset 1 priced in USD: ValueUSD / adjustedVolBase(asset1) = 1000 / 100 = 10
                Assert.That(b.Price, Is.EqualTo(10m));
                Assert.That(b.VolumeBase, Is.EqualTo(100m));
                Assert.That(b.VolumeQuote, Is.EqualTo(1000m));
                Assert.That(b.AssetIdB, Is.EqualTo(usdcAssetId));
                Assert.That(b.DocId, Does.StartWith($"1-{usdcAssetId}-{b.Interval}-usd-"));
            }

            foreach (var b in usdSeriesForAssetB)
            {
                // asset 2 priced in USD: ValueUSD / adjustedVolQuote(asset2) = 1000 / 250 = 4
                Assert.That(b.Price, Is.EqualTo(4m));
                Assert.That(b.VolumeBase, Is.EqualTo(250m));
                Assert.That(b.VolumeQuote, Is.EqualTo(1000m));
                Assert.That(b.AssetIdB, Is.EqualTo(usdcAssetId));
                Assert.That(b.DocId, Does.StartWith($"2-{usdcAssetId}-{b.Interval}-usd-"));
            }
        }

        [Test]
        public async Task GetIntervalBuckets_ReversedTrade_Canonicalizes()
        {
            var trade = new Trade
            {
                AssetIdIn = 2,
                AssetIdOut = 1,
                AssetAmountIn = 300,
                AssetAmountOut = 120,
                ValueUSD = 600m,
                Timestamp = DateTimeOffset.Parse("2024-05-06T07:08:09Z"),
                TradeState = AVMTradeReporter.Models.Data.Enums.TxState.Confirmed
            };
            var buckets = await _repo.GetIntervalBuckets(trade);
            Assert.That(buckets, Is.Not.Empty);

            var assetSeries = buckets.Where(b => b.InUsdValuation == false).ToList();
            var usdSeries = buckets.Where(b => b.InUsdValuation == true).ToList();

            // asset price calculation: VolumeQuote/VolumeBase => 300/120 = 2.5
            Assert.That(assetSeries.All(b => b.Price == 2.5m));
            Assert.That(assetSeries.All(b => b.AssetIdA == 1UL));
            Assert.That(assetSeries.All(b => b.AssetIdB == 2UL));

            const ulong usdcAssetId = 31566704UL;

            // asset 1 (base) priced in usd: ValueUSD/adjustedVolBase => 600/120 = 5
            var usdSeriesForAssetA = usdSeries.Where(b => b.AssetIdA == 1UL).ToList();
            Assert.That(usdSeriesForAssetA.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(usdSeriesForAssetA.All(b => b.Price == 5m));
            Assert.That(usdSeriesForAssetA.All(b => b.AssetIdB == usdcAssetId));

            // asset 2 (quote) priced in usd: ValueUSD/adjustedVolQuote => 600/300 = 2
            var usdSeriesForAssetB = usdSeries.Where(b => b.AssetIdA == 2UL).ToList();
            Assert.That(usdSeriesForAssetB.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(usdSeriesForAssetB.All(b => b.Price == 2m));
            Assert.That(usdSeriesForAssetB.All(b => b.AssetIdB == usdcAssetId));
        }

        [Test]
        public async Task GetIntervalBuckets_InvalidSameAsset_ReturnsEmpty()
        {
            var trade = new Trade
            {
                AssetIdIn = 5,
                AssetIdOut = 5,
                AssetAmountIn = 10,
                AssetAmountOut = 10,
                Timestamp = DateTimeOffset.UtcNow,
                TradeState = AVMTradeReporter.Models.Data.Enums.TxState.Confirmed
            };
            var buckets = await _repo.GetIntervalBuckets(trade);
            Assert.That(buckets, Is.Empty);
        }

        [Test]
        public async Task GetIntervalBuckets_ZeroBaseVolume_ReturnsEmpty()
        {
            var trade = new Trade
            {
                AssetIdIn = 1,
                AssetIdOut = 2,
                AssetAmountIn = 0,
                AssetAmountOut = 0,
                Timestamp = DateTimeOffset.UtcNow,
                TradeState = AVMTradeReporter.Models.Data.Enums.TxState.Confirmed
            };
            var buckets = await _repo.GetIntervalBuckets(trade);
            Assert.That(buckets, Is.Empty);
        }
    }
}
