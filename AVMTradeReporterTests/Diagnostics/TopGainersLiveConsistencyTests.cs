using System.Text.Json;

namespace AVMTradeReporterTests.Diagnostics;

/// <summary>
/// Live-data acceptance check for the Top gainers percentages (production incident 2026-08-12:
/// the top gainers showed systematic ~+99%/+90%/+77% "gains" whose from/to prices were both
/// artifacts — the current price came from a dust direct-USDC pool overriding the liquid ALGO
/// market by 100-200x, and the 24h baseline came from ValueUSD-averaged 1m candles at roughly
/// half of that wrong price).
///
/// Marked [Explicit] because it calls the production API. For every listed top gainer it
/// recomputes the asset's trade-derived USD price now and 24h ago from the never-polluted
/// `-asset-` exchange-rate candles (direct USDC pair preferred, else ALGO pair × ALGO/USDC) and
/// asserts that the served priceUSD, priceUSD24H, and the resulting percentage are within a
/// loose factor of that ground truth. Expected to FAIL until the fixes
/// (SelectAssetUsdPrice route selection + 1h GetHistoricalPriceAsync + OhlcUsdRepairService)
/// are deployed to the target environment; passes afterwards.
/// </summary>
[TestFixture]
[Explicit("Hits the live production API; diagnostic/acceptance, not a regression gate.")]
[Category("LiveData")]
public class TopGainersLiveConsistencyTests
{
    private const string ApiBase = "https://api.algorand.scan.biatec.io";
    private const ulong Usdc = 31566704UL;
    /// <summary>Pool spot vs trade close may legitimately differ; 100x mispricing must not pass.</summary>
    private const double ToleranceFactor = 1.5;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    [Test]
    public async Task TopGainers_PricesAndPercent_AgreeWithTradeDerivedGroundTruth()
    {
        using var top = JsonDocument.Parse(await Http.GetStringAsync($"{ApiBase}/api/asset/top"));
        var gainers = top.RootElement.GetProperty("topGainers").EnumerateArray().ToList();
        Assert.That(gainers, Is.Not.Empty, "no top gainers listed — nothing to verify");

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var targetUnix = nowUnix - 24 * 3600;
        var algoUsd = await LoadAssetCloseSeriesAsync(0, Usdc, nowUnix);

        var failures = new List<string>();
        foreach (var g in gainers)
        {
            var assetId = g.GetProperty("assetId").GetUInt64();
            var priceNow = g.GetProperty("priceUSD").GetDouble();
            var price24H = g.TryGetProperty("priceUSD24H", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : (double?)null;
            var percent = g.TryGetProperty("priceChange24HPercent", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : (double?)null;

            var trueNow = await TradeDerivedPriceAsync(assetId, nowUnix, algoUsd);
            var truePast = await TradeDerivedPriceAsync(assetId, targetUnix, algoUsd);
            TestContext.Out.WriteLine(
                $"asset {assetId}: served now={priceNow:E4} 24h={price24H:E4} ({percent:F2}%) | trade-derived now={trueNow:E4} 24h={truePast:E4}");

            if (trueNow.HasValue && OffByMoreThanFactor(priceNow, trueNow.Value))
                failures.Add($"asset {assetId}: priceUSD {priceNow:E4} vs trade-derived {trueNow.Value:E4}");
            if (price24H.HasValue && truePast.HasValue && OffByMoreThanFactor(price24H.Value, truePast.Value))
                failures.Add($"asset {assetId}: priceUSD24H {price24H.Value:E4} vs trade-derived {truePast.Value:E4}");
            if (percent.HasValue && trueNow.HasValue && truePast.HasValue && truePast.Value > 0)
            {
                var truePercent = (trueNow.Value - truePast.Value) / truePast.Value * 100;
                if (Math.Abs(percent.Value - truePercent) > 25 && OffByMoreThanFactor(1 + percent.Value / 100, 1 + truePercent / 100))
                    failures.Add($"asset {assetId}: change {percent.Value:F1}% vs trade-derived {truePercent:F1}%");
            }
        }

        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
    }

    private static bool OffByMoreThanFactor(double served, double truth)
    {
        if (truth <= 0 || served <= 0) return false;
        var ratio = served / truth;
        return ratio > ToleranceFactor || ratio < 1 / ToleranceFactor;
    }

    /// <summary>Trade-derived USD price at (or last before) the given time, from `-asset-` candles.</summary>
    private static async Task<double?> TradeDerivedPriceAsync(ulong assetId, long atUnix, SortedList<long, double> algoUsdCloses)
    {
        if (assetId == Usdc) return 1;
        // Prefer the direct USDC pair when it traded; fall back to the ALGO pair × ALGO/USDC.
        var direct = await LoadAssetCloseSeriesAsync(assetId, Usdc, atUnix);
        var directClose = LastAtOrBefore(direct, atUnix);
        if (directClose.HasValue) return directClose;

        if (assetId == 0) return LastAtOrBefore(algoUsdCloses, atUnix);
        var viaAlgo = await LoadAssetCloseSeriesAsync(assetId, 0, atUnix);
        var algoPerAsset = LastAtOrBefore(viaAlgo, atUnix);
        var algo = LastAtOrBefore(algoUsdCloses, atUnix);
        if (algoPerAsset == null || algo == null) return null;
        return algoPerAsset * algo;
    }

    /// <summary>
    /// Loads 1h `-asset-` closes for the pair. The history endpoint always returns prices as
    /// "second argument per first argument" (it inverts stored canonical candles when needed),
    /// so for (X, USDC) the close is USDC per X and for (X, 0) it is ALGO per X.
    /// </summary>
    private static async Task<SortedList<long, double>> LoadAssetCloseSeriesAsync(ulong assetId, ulong counterId, long toUnix)
    {
        var from = toUnix - 40 * 3600;
        var url = $"{ApiBase}/api/OHLC/history?assetA={assetId}&assetB={counterId}&resolution=60-asset&from={from}&to={toUnix + 3600}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var result = new SortedList<long, double>();
        if (!doc.RootElement.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Array) return result;
        var c = doc.RootElement.GetProperty("c");
        for (var i = 0; i < t.GetArrayLength(); i++)
        {
            result[t[i].GetInt64()] = c[i].GetDouble();
        }
        return result;
    }

    private static double? LastAtOrBefore(SortedList<long, double> series, long atUnix)
    {
        double? best = null;
        foreach (var kv in series)
        {
            if (kv.Key <= atUnix) best = kv.Value;
            else break;
        }
        return best;
    }
}
