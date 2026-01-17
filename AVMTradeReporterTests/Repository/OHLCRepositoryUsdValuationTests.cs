using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;

namespace AVMTradeReporterTests.Repository;

public class OHLCRepositoryUsdValuationTests
{
    [Test]
    public void GetIntervalBuckets_WhenNoUsdValue_GeneratesOnlyAssetSeries()
    {
        var trade = new Trade
        {
            AssetIdIn = 1,
            AssetIdOut = 2,
            AssetAmountIn = 100,
            AssetAmountOut = 250,
            ValueUSD = null,
            Timestamp = DateTimeOffset.Parse("2024-01-02T03:04:05Z"),
            TradeState = AVMTradeReporter.Models.Data.Enums.TxState.Confirmed
        };

        var buckets = OHLCRepository.GetIntervalBuckets(trade).ToList();

        Assert.That(buckets.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
        Assert.That(buckets.All(b => b.InUsdValuation == false));
        Assert.That(buckets.All(b => b.DocId.Contains("-asset-")));
    }
}
