using AVMTradeReporter.Model.Data;
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
                Timestamp = DateTimeOffset.Parse("2024-01-02T03:04:05Z"),
                TradeState = AVMTradeReporter.Model.Data.Enums.TxState.Confirmed
            };

            var buckets = OHLCRepository.GetIntervalBuckets(trade).ToList();
            Assert.That(buckets.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            // Validate price
            foreach (var b in buckets)
            {
                Assert.That(b.Price, Is.EqualTo(2.5m));
                Assert.That(b.VolumeBase, Is.EqualTo(100m));
                Assert.That(b.VolumeQuote, Is.EqualTo(250m));
                Assert.That(OHLCRepository.Intervals.Select(i => i.code), Does.Contain(b.Interval));
                Assert.That(string.Join('-', b.DocId.Split('-').Take(3)), Is.EqualTo("1-2-" + b.Interval));
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
                Timestamp = DateTimeOffset.Parse("2024-05-06T07:08:09Z"),
                TradeState = AVMTradeReporter.Model.Data.Enums.TxState.Confirmed
            };
            var buckets = OHLCRepository.GetIntervalBuckets(trade).ToList();
            Assert.That(buckets, Is.Not.Empty);
            // price calculation: VolumeQuote/VolumeBase => 300/120 = 2.5
            Assert.That(buckets.All(b => b.Price == 2.5m));
            Assert.That(buckets.All(b => b.AssetIdA == 1UL));
            Assert.That(buckets.All(b => b.AssetIdB == 2UL));
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
                TradeState = AVMTradeReporter.Model.Data.Enums.TxState.Confirmed
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
                TradeState = AVMTradeReporter.Model.Data.Enums.TxState.Confirmed
            };
            Assert.That(OHLCRepository.GetIntervalBuckets(trade), Is.Empty);
        }
    }
}
