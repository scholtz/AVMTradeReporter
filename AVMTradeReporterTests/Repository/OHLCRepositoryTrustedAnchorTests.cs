using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Options;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// USD OHLC series must be anchored to the trusted leg of a trade, never to Trade.ValueUSD.
    ///
    /// Background (production incident, 2026-08-11): Trade.ValueUSD is the average of both legs'
    /// USD values, each valued at the asset's cached PriceUSD. When a major asset (ALGO, goBTC)
    /// traded against an illiquid token whose cached price was wrong by 2-200x, the averaged
    /// ValueUSD divided by the major leg's amount wrote absurd highs/lows into the MAJOR asset's
    /// own USD candles (ALGO ~$0.09 charted hourly highs of ~$9.2; goBTC ~$63k charted ~$118k),
    /// rendering the 7d sparklines as unreadable stripes.
    ///
    /// Contract verified here: for a trade X↔Y, X's USD candle is written only when Y is a
    /// trusted reference asset ({ALGO, UsdReferenceAssetId} ∪ TrustedReferenceAssetIds) with a
    /// known USD price, priced as (volY / volX) × priceUSD(Y) — the exact on-chain exchange rate
    /// with a reliable USD anchor. Trades between two untrusted assets write no USD candles.
    /// </summary>
    public class OHLCRepositoryTrustedAnchorTests
    {
        private const ulong Usdc = 31566704UL;      // default UsdReferenceAssetId, trusted
        private const ulong Algo = 0UL;             // always trusted
        private const ulong GoBtc = 386192725UL;    // in default TrustedReferenceAssetIds
        private const ulong GoEth = 386195940UL;    // in default TrustedReferenceAssetIds
        private const ulong ScamToken = 1294383366UL;   // NOT trusted
        private const ulong OtherToken = 999999999UL;   // NOT trusted

        private MockAssetRepository _assets = null!;
        private OHLCRepository _repo = null!;

        [SetUp]
        public async Task SetUp()
        {
            _assets = new MockAssetRepository();
            _repo = new OHLCRepository(null!, null!, _assets, Options.Create(new AppConfiguration()));

            await SetAssetAsync(Algo, decimals: 6, priceUsd: 0.09m);
            await SetAssetAsync(GoBtc, decimals: 8, priceUsd: 63000m);
            await SetAssetAsync(Usdc, decimals: 6, priceUsd: 1m);
            // Deliberately mispriced illiquid token — its cached price must never leak into a
            // trusted asset's USD series.
            await SetAssetAsync(ScamToken, decimals: 6, priceUsd: 1234m);
            await SetAssetAsync(OtherToken, decimals: 6, priceUsd: 2m);
        }

        private async Task SetAssetAsync(ulong assetId, int decimals, decimal priceUsd)
        {
            var asset = await _assets.GetAssetAsync(assetId);
            asset!.Params.Decimals = (ulong)decimals;
            asset.PriceUSD = priceUsd;
            await _assets.SetAssetAsync(asset);
        }

        private static Trade MakeTrade(ulong assetIn, ulong amountIn, ulong assetOut, ulong amountOut, decimal? valueUsd = null) => new()
        {
            AssetIdIn = assetIn,
            AssetIdOut = assetOut,
            AssetAmountIn = amountIn,
            AssetAmountOut = amountOut,
            ValueUSD = valueUsd,
            Timestamp = DateTimeOffset.Parse("2026-08-08T02:30:00Z"),
            TradeState = AVMTradeReporter.Models.Data.Enums.TxState.Confirmed
        };

        [Test]
        public async Task TrustedVsUntrusted_DoesNotWriteUsdCandleForTheTrustedAsset()
        {
            // The production goBTC case: a dust trade (0.001 goBTC ≈ $63) against a mispriced
            // token, with an inflated averaged ValueUSD that previously implied $118k/BTC.
            var trade = MakeTrade(GoBtc, 100_000, ScamToken, 63_000_000, valueUsd: 118m);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            var goBtcUsd = buckets.Where(b => b.InUsdValuation && b.AssetIdA == GoBtc).ToList();
            Assert.That(goBtcUsd, Is.Empty,
                "goBTC's USD series must not be written from a trade whose only USD anchor is an untrusted token");
        }

        [Test]
        public async Task TrustedVsUntrusted_PricesTheUntrustedAssetFromTheTrustedLeg()
        {
            // 0.001 goBTC ($63 at the cached $63000) buys 63 SCAM → SCAM is worth $1, no matter
            // how wrong SCAM's own cached price (1234) is and no matter what ValueUSD says.
            var trade = MakeTrade(GoBtc, 100_000, ScamToken, 63_000_000, valueUsd: 118m);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            var scamUsd = buckets.Where(b => b.InUsdValuation && b.AssetIdA == ScamToken).ToList();
            Assert.That(scamUsd.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(scamUsd.All(b => b.Price == 1m), "SCAM price must be rate × priceUSD(goBTC) = 1");
            Assert.That(scamUsd.All(b => b.VolumeBase == 63m));
            Assert.That(scamUsd.All(b => b.VolumeQuote == 63m), "USD volume must be volume × anchored price");
            Assert.That(scamUsd.All(b => b.AssetIdB == Usdc));
            Assert.That(scamUsd.All(b => b.DocId.StartsWith($"{ScamToken}-{Usdc}-{b.Interval}-usd-")));
        }

        [Test]
        public async Task AlgoVsUntrusted_DoesNotWriteUsdCandleForAlgo()
        {
            // The production ALGO case: dust arbs against mispriced tokens previously charted
            // ALGO (~$0.09) at hourly highs of ~$9.2.
            var trade = MakeTrade(Algo, 10_000_000, ScamToken, 90_000_000, valueUsd: 92m);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            Assert.That(buckets.Where(b => b.InUsdValuation && b.AssetIdA == Algo), Is.Empty);

            var scamUsd = buckets.Where(b => b.InUsdValuation && b.AssetIdA == ScamToken).ToList();
            Assert.That(scamUsd.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            // 10 ALGO ($0.90) buys 90 SCAM → $0.01 per SCAM.
            Assert.That(scamUsd.All(b => b.Price == 0.01m));
        }

        [Test]
        public async Task BothLegsTrusted_WritesBothUsdCandles_EachAnchoredToTheCounterLeg()
        {
            // 70 ALGO ↔ 0.0001 goBTC.
            var trade = MakeTrade(Algo, 70_000_000, GoBtc, 10_000);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            var algoUsd = buckets.Where(b => b.InUsdValuation && b.AssetIdA == Algo).ToList();
            var goBtcUsd = buckets.Where(b => b.InUsdValuation && b.AssetIdA == GoBtc).ToList();

            Assert.That(algoUsd.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(goBtcUsd.Count, Is.EqualTo(OHLCRepository.Intervals.Length));

            // ALGO anchored to goBTC: (0.0001 × 63000) / 70 = 0.09
            Assert.That(algoUsd.All(b => b.Price == 0.09m));
            Assert.That(algoUsd.All(b => b.VolumeBase == 70m));
            Assert.That(algoUsd.All(b => b.VolumeQuote == 70m * 0.09m));

            // goBTC anchored to ALGO: (70 × 0.09) / 0.0001 = 63000
            Assert.That(goBtcUsd.All(b => b.Price == 63000m));
            Assert.That(goBtcUsd.All(b => b.VolumeBase == 0.0001m));
            Assert.That(goBtcUsd.All(b => b.VolumeQuote == 0.0001m * 63000m));
        }

        [Test]
        public async Task TradeAgainstUsdReference_AnchorsToOneDollar_EvenWithoutValueUsd()
        {
            // 50 X ↔ 100 USDC; ValueUSD deliberately null — the anchor must not depend on it.
            var trade = MakeTrade(OtherToken, 50_000_000, Usdc, 100_000_000, valueUsd: null);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            var otherUsd = buckets.Where(b => b.InUsdValuation && b.AssetIdA == OtherToken).ToList();
            Assert.That(otherUsd.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(otherUsd.All(b => b.Price == 2m), "X price must be (100 USDC × $1) / 50 X = $2");

            // The USD reference's own series must not be anchored to the untrusted counter.
            Assert.That(buckets.Where(b => b.InUsdValuation && b.AssetIdA == Usdc), Is.Empty);
        }

        [Test]
        public async Task UsdReferenceVsTrusted_WritesTheUsdReferencesOwnSeries()
        {
            // 630 USDC ↔ 0.01 goBTC: USDC/USD chart keeps getting data from trusted pairs.
            var trade = MakeTrade(Usdc, 630_000_000, GoBtc, 1_000_000);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            var usdcUsd = buckets.Where(b => b.InUsdValuation && b.AssetIdA == Usdc).ToList();
            Assert.That(usdcUsd.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            // (0.01 goBTC × 63000) / 630 USDC = $1 per USDC
            Assert.That(usdcUsd.All(b => b.Price == 1m));
            Assert.That(usdcUsd.All(b => b.DocId.StartsWith($"{Usdc}-{Usdc}-{b.Interval}-usd-")));
        }

        [Test]
        public async Task UntrustedPair_WritesNoUsdCandles_EvenWhenValueUsdIsSet()
        {
            var trade = MakeTrade(OtherToken, 1_000_000, ScamToken, 2_000_000, valueUsd: 1000m);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            Assert.That(buckets.Where(b => b.InUsdValuation), Is.Empty,
                "no trusted anchor exists — writing any USD candle would launder an unreliable price");
            Assert.That(buckets.Where(b => !b.InUsdValuation).Count(), Is.EqualTo(OHLCRepository.Intervals.Length),
                "the plain asset-rate series is exact on-chain truth and must still be written");
        }

        [Test]
        public async Task TrustedCounterWithoutKnownPrice_WritesNoUsdCandleForTheOtherLeg()
        {
            await SetAssetAsync(GoEth, decimals: 8, priceUsd: 0m);
            var trade = MakeTrade(OtherToken, 1_000_000, GoEth, 2_000_000);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            Assert.That(buckets.Where(b => b.InUsdValuation && b.AssetIdA == OtherToken), Is.Empty,
                "a trusted counter with no known USD price cannot anchor anything");
        }
    }
}
