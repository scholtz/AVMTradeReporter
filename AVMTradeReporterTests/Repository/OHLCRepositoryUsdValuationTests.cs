using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;

namespace AVMTradeReporterTests.Repository;

public class OHLCRepositoryUsdValuationTests
{
    private OHLCRepository _repo = new(null, null);

    /// <summary>
    /// Trade.ValueUSD must play no role in OHLC generation at all: it averages both legs' cached
    /// USD values, which is exactly the mechanism that used to let one mispriced counter-token
    /// corrupt a major asset's USD candles. USD series come solely from trusted-counter anchoring
    /// (see OHLCRepositoryTrustedAnchorTests), so for an untrusted pair the buckets are identical —
    /// asset series only — whether ValueUSD is null or set.
    /// </summary>
    [TestCase(null)]
    [TestCase("1000")]
    public void GetIntervalBuckets_UntrustedPair_GeneratesOnlyAssetSeries_RegardlessOfValueUsd(string? valueUsd)
    {
        var trade = new Trade
        {
            AssetIdIn = 1,
            AssetIdOut = 2,
            AssetAmountIn = 100,
            AssetAmountOut = 250,
            ValueUSD = valueUsd == null ? null : decimal.Parse(valueUsd),
            Timestamp = DateTimeOffset.Parse("2024-01-02T03:04:05Z"),
            TradeState = AVMTradeReporter.Models.Data.Enums.TxState.Confirmed
        };

        var buckets = _repo.GetIntervalBuckets(trade).Result.ToList();

        Assert.That(buckets.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
        Assert.That(buckets.All(b => b.InUsdValuation == false));
        Assert.That(buckets.All(b => b.DocId.Contains("-asset-")));
    }
}
