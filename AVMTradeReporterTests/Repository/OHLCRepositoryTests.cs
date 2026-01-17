using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AVMTradeReporterTests.Repository
{
    public class OHLCRepositoryTests
    {
        [Test]
        public void GetIntervalBuckets_BasicTrade_GeneratesAllBuckets()
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

            var buckets = OHLCRepository.GetIntervalBuckets(trade).ToList();
            Assert.That(buckets.Count, Is.EqualTo(OHLCRepository.Intervals.Length * 2));

            var assetSeries = buckets.Where(b => b.InUsdValuation == false).ToList();
            var usdSeries = buckets.Where(b => b.InUsdValuation == true).ToList();

            Assert.That(assetSeries.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(usdSeries.Count, Is.EqualTo(OHLCRepository.Intervals.Length));

            foreach (var b in assetSeries)
            {
                Assert.That(b.Price, Is.EqualTo(2.5m));
                Assert.That(b.VolumeBase, Is.EqualTo(100m));
                Assert.That(b.VolumeQuote, Is.EqualTo(250m));
                Assert.That(OHLCRepository.Intervals.Select(i => i.code), Does.Contain(b.Interval));
                Assert.That(b.DocId, Does.StartWith($"1-2-{b.Interval}-asset-"));
            }

            foreach (var b in usdSeries)
            {
                Assert.That(b.Price, Is.EqualTo(10m));
                Assert.That(b.VolumeBase, Is.EqualTo(100m));
                Assert.That(b.VolumeQuote, Is.EqualTo(1000m));
                Assert.That(OHLCRepository.Intervals.Select(i => i.code), Does.Contain(b.Interval));
                Assert.That(b.DocId, Does.StartWith($"1-2-{b.Interval}-usd-"));
            }
        }

        [Test]
        public void GetIntervalBuckets_ReversedTrade_Canonicalizes()
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
            var buckets = OHLCRepository.GetIntervalBuckets(trade).ToList();
            Assert.That(buckets, Is.Not.Empty);

            var assetSeries = buckets.Where(b => b.InUsdValuation == false).ToList();
            var usdSeries = buckets.Where(b => b.InUsdValuation == true).ToList();

            // asset price calculation: VolumeQuote/VolumeBase => 300/120 = 2.5
            Assert.That(assetSeries.All(b => b.Price == 2.5m));
            Assert.That(assetSeries.All(b => b.AssetIdA == 1UL));
            Assert.That(assetSeries.All(b => b.AssetIdB == 2UL));

            // usd price calculation: ValueUSD/VolumeBase => 600/120 = 5
            Assert.That(usdSeries.All(b => b.Price == 5m));
            Assert.That(usdSeries.All(b => b.AssetIdA == 1UL));
            Assert.That(usdSeries.All(b => b.AssetIdB == 2UL));
        }

        [Test]
        public void GetIntervalBuckets_InvalidSameAsset_ReturnsEmpty()
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
            Assert.That(OHLCRepository.GetIntervalBuckets(trade), Is.Empty);
        }

        [Test]
        public void GetIntervalBuckets_ZeroBaseVolume_ReturnsEmpty()
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
            Assert.That(OHLCRepository.GetIntervalBuckets(trade), Is.Empty);
        }
    }
}
