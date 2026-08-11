using System.Text.Json;
using AVMTradeReporter.Model.DTO;

namespace AVMTradeReporterTests.Diagnostics;

/// <summary>
/// Live-data diagnostic for the "stripy" 7d price sparklines on biatec-scan-web.
///
/// Marked [Explicit] because it calls the production API (network + data dependent) — run it with:
///   dotnet test AVMTradeReporterTests/AVMTradeReporterTests.csproj \
///     --filter FullyQualifiedName~AssetTimeseries7DLiveStripesTests -- NUnit.Where="cat==LiveData"
///
/// What it demonstrated (root cause chain, FIXED by trusted-anchor USD pricing in OHLCRepository —
/// see OHLCRepositoryTrustedAnchorTests for the unit-level contract):
/// 1. The 1h USD OHLC candles served by api/asset/timeseries/7d contained extreme high/low outliers
///    (e.g. ALGO ~ $0.09 with hourly highs of ~$9 — 100x off). The sparkline scales to
///    min(low)/max(high), so genuine price movement flattened into a line and the outlier wicks
///    rendered as full-height stripes.
/// 2. Each outlier traced back to individual trades where Trade.ValueUSD disagreed wildly with the
///    market value of the major-asset leg. ValueUSD is CombineSides() = the AVERAGE of both legs'
///    USD values (TradeReporterBackgroundService.PopulateTradeUsdAsync), each leg valued at the
///    asset's cached BiatecAsset.PriceUSD. When the counter-asset was an illiquid/scam token with a
///    stale or manipulated cached price, the average was dominated by the bogus leg, and
///    OHLCRepository.GetIntervalBuckets wrote usdPrice = ValueUSD / amount into the MAJOR asset's
///    own USD candle series, corrupting its high/low (and close, when it was the hour's last trade).
///
/// After the fixed backend has been deployed AND the polluted candles have rolled out of the 7d
/// window (up to a week), the first test's expectation inverts: it should find ZERO outliers.
/// Flip the assertion at that point (or delete this diagnostic once the fix is verified live).
/// </summary>
[TestFixture]
[Explicit("Hits the live production API; diagnostic, not a regression gate.")]
[Category("LiveData")]
public class AssetTimeseries7DLiveStripesTests
{
    private const string ApiBase = "https://api.algorand.scan.biatec.io";
    private const ulong AlgoAssetId = 0;
    private const int AlgoDecimals = 6;

    /// <summary>A candle whose high/low is this many times away from the series' median close is an outlier.</summary>
    private const decimal OutlierFactor = 2m;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record OutlierCandle(long UnixHour, decimal Open, decimal High, decimal Low, decimal Close, decimal MedianClose);

    [Test]
    public async Task PriceCandles_ForMajorAssets_ContainOutlierWicks_ThatCauseTheStripes()
    {
        // ALGO, goBTC, goETH, USDt — all visibly stripy in the UI.
        var assetIds = new ulong[] { 0, 386192725, 386195940, 312769 };
        var series = await FetchTimeseriesAsync(assetIds);

        var report = new List<string>();
        foreach (var s in series)
        {
            var outliers = FindOutliers(s.Price);
            var median = MedianClose(s.Price);
            report.Add($"asset {s.AssetId}: {s.Price.T.Count} candles, median close {median}, {outliers.Count} outlier candle(s) beyond {OutlierFactor}x");
            foreach (var o in outliers.Take(10))
            {
                report.Add($"   {DateTimeOffset.FromUnixTimeSeconds(o.UnixHour * 3600):yyyy-MM-dd HH}h  O={o.Open} H={o.High} L={o.Low} C={o.Close}");
            }
        }
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, report));

        // The bug is present while any major asset's 7d series contains wicks OutlierFactor+ away
        // from its own median close — that is what visually squashes the sparkline into stripes.
        var totalOutliers = series.Sum(s => FindOutliers(s.Price).Count);
        Assert.That(totalOutliers, Is.GreaterThan(0),
            "No outlier wicks found — either the OHLC pollution is fixed (great: delete this diagnostic) or the window is unusually clean.");
    }

    [Test]
    public async Task WorstAlgoCandle_TracesBackToTrades_WhoseValueUsdContradictsTheirAlgoLeg()
    {
        var series = await FetchTimeseriesAsync(new[] { AlgoAssetId });
        var algo = series.Single(s => s.AssetId == AlgoAssetId);
        var outliers = FindOutliers(algo.Price);
        Assert.That(outliers, Is.Not.Empty, "No outlier candle in ALGO's current 7d window — rerun when the UI looks stripy.");

        var worst = outliers.OrderByDescending(o => Math.Max(o.High / o.MedianClose, o.MedianClose / Math.Max(o.Low, 0.0000001m))).First();
        var from = DateTimeOffset.FromUnixTimeSeconds(worst.UnixHour * 3600);
        var to = from.AddHours(1);
        TestContext.Out.WriteLine($"Worst ALGO candle {from:yyyy-MM-dd HH}h: H={worst.High} L={worst.Low} (median close {worst.MedianClose})");

        // Pull the raw trades of that hour and recompute the price OHLCRepository derived for the
        // ALGO USD series: usdPrice = trade.ValueUSD / algoAmount.
        var url = $"{ApiBase}/api/trade?assetId={AlgoAssetId}&timestampFrom={from:yyyy-MM-ddTHH:mm:ssZ}&timestampTo={to:yyyy-MM-ddTHH:mm:ssZ}&size=500";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var items = doc.RootElement.TryGetProperty("items", out var i) ? i : doc.RootElement;

        var offenders = new List<string>();
        decimal maxImplied = 0;
        foreach (var t in items.EnumerateArray())
        {
            if (!t.TryGetProperty("valueUSD", out var v) || v.ValueKind != JsonValueKind.Number) continue;
            var valueUsd = v.GetDecimal();
            var inId = t.GetProperty("assetIdIn").GetUInt64();
            var outId = t.GetProperty("assetIdOut").GetUInt64();
            var algoBaseUnits = inId == AlgoAssetId
                ? t.GetProperty("assetAmountIn").GetUInt64()
                : outId == AlgoAssetId ? t.GetProperty("assetAmountOut").GetUInt64() : 0;
            if (algoBaseUnits == 0) continue;

            var algoAmount = algoBaseUnits / (decimal)Math.Pow(10, AlgoDecimals);
            var impliedAlgoPrice = valueUsd / algoAmount;
            if (impliedAlgoPrice > maxImplied) maxImplied = impliedAlgoPrice;

            if (impliedAlgoPrice > worst.MedianClose * OutlierFactor || impliedAlgoPrice < worst.MedianClose / OutlierFactor)
            {
                var counterAsset = inId == AlgoAssetId ? outId : inId;
                offenders.Add(
                    $"   tx {t.GetProperty("txId").GetString()} ({t.GetProperty("protocol")}) vs asset {counterAsset}: " +
                    $"{algoAmount} ALGO leg ≈ ${algoAmount * worst.MedianClose:F2} market, but ValueUSD=${valueUsd:F2} " +
                    $"→ implied ALGO price ${impliedAlgoPrice:F4}");
            }
        }

        TestContext.Out.WriteLine($"{offenders.Count} offending trade(s):");
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, offenders));

        Assert.That(offenders, Is.Not.Empty,
            "Expected trades whose ValueUSD (average of both legs, incl. a mispriced counter-asset leg) contradicts their ALGO leg.");
        // The candle's polluted high is exactly the max implied per-trade price — proving the OHLC
        // outlier comes from ValueUSD/amount of these trades, not from any real market move.
        Assert.That((double)maxImplied, Is.EqualTo((double)worst.High).Within(1).Percent,
            "Candle high should match the worst trade's implied price (ValueUSD / ALGO amount).");
    }

    private static async Task<List<AssetTimeseries7D>> FetchTimeseriesAsync(IEnumerable<ulong> assetIds)
    {
        var url = $"{ApiBase}/api/asset/timeseries/7d?assetIds={string.Join(',', assetIds)}";
        var json = await Http.GetStringAsync(url);
        var result = JsonSerializer.Deserialize<List<AssetTimeseries7D>>(json, JsonOpts);
        Assert.That(result, Is.Not.Null.And.Not.Empty, $"No timeseries returned from {url}");
        return result!;
    }

    private static decimal MedianClose(TimeseriesCandles candles)
    {
        var sorted = candles.C.OrderBy(x => x).ToList();
        Assert.That(sorted, Is.Not.Empty, "Price series is empty");
        return sorted[sorted.Count / 2];
    }

    private static List<OutlierCandle> FindOutliers(TimeseriesCandles candles)
    {
        if (candles.T.Count == 0) return new List<OutlierCandle>();
        var median = MedianClose(candles);
        var result = new List<OutlierCandle>();
        for (var i = 0; i < candles.T.Count; i++)
        {
            if (candles.H[i] > median * OutlierFactor || candles.L[i] < median / OutlierFactor)
            {
                result.Add(new OutlierCandle(candles.T[i] / 3600, candles.O[i], candles.H[i], candles.L[i], candles.C[i], median));
            }
        }
        return result;
    }
}
