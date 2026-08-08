using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Services;
using NUnit.Framework;

namespace AVMTradeReporterTests.Services
{
    /// <summary>
    /// Unit tests for <see cref="TradeQueryService.BucketAssetVolumes"/> — the pure per-asset
    /// windowed volume computation behind the top-assets highlights.
    /// </summary>
    [TestFixture]
    public class TradeQueryServiceVolumeBucketingTests
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

        private static Trade MakeTrade(ulong assetIn, ulong assetOut, decimal valueUsd, TimeSpan age)
        {
            return new Trade
            {
                AssetIdIn = assetIn,
                AssetIdOut = assetOut,
                ValueUSD = valueUsd,
                Timestamp = Now - age,
            };
        }

        [Test]
        public void BucketAssetVolumes_AssignsTradesToCorrectWindows()
        {
            var trades = new[]
            {
                MakeTrade(1, 2, 100m, TimeSpan.FromMinutes(30)),   // current 1h + current 24h
                MakeTrade(1, 2, 60m, TimeSpan.FromMinutes(90)),    // previous 1h + current 24h
                MakeTrade(1, 2, 40m, TimeSpan.FromHours(10)),      // current 24h only
                MakeTrade(1, 2, 30m, TimeSpan.FromHours(30)),      // previous 24h
            };

            var result = TradeQueryService.BucketAssetVolumes(trades, Now);

            // Each asset is credited half of every trade's value.
            Assert.That(result[1].Volume1H, Is.EqualTo(50m));
            Assert.That(result[1].Volume1HPrev, Is.EqualTo(30m));
            Assert.That(result[1].Volume24H, Is.EqualTo(100m));
            Assert.That(result[1].Volume24HPrev, Is.EqualTo(15m));
            Assert.That(result[2].Volume1H, Is.EqualTo(50m));
            Assert.That(result[2].Volume24H, Is.EqualTo(100m));
        }

        [Test]
        public void BucketAssetVolumes_IgnoresTradesOutsideWindowOrWithoutValue()
        {
            var trades = new[]
            {
                MakeTrade(1, 2, 100m, TimeSpan.FromHours(49)),     // older than 48h
                MakeTrade(1, 2, 100m, TimeSpan.FromMinutes(-5)),   // in the future
                new Trade { AssetIdIn = 1, AssetIdOut = 2, ValueUSD = null, Timestamp = Now.AddMinutes(-5) },
                new Trade { AssetIdIn = 1, AssetIdOut = 2, ValueUSD = 100m, Timestamp = null },
            };

            var result = TradeQueryService.BucketAssetVolumes(trades, Now);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void BucketAssetVolumes_SameAssetBothSides_CreditedOnce()
        {
            var trades = new[] { MakeTrade(7, 7, 100m, TimeSpan.FromMinutes(5)) };

            var result = TradeQueryService.BucketAssetVolumes(trades, Now);

            Assert.That(result[7].Volume1H, Is.EqualTo(50m));
        }
    }
}
