using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Options;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Trusted assets' candles must not record off-market prints from stale/dust pools.
    ///
    /// Background (production incident, 2026-08-13): even after the trusted-anchor fix, ALGO's
    /// USD candles charted constant highs of ~$9.15 and lows of ~half price. The offending
    /// trades were real on-chain swaps between ALGO and a trusted counter executed in stale or
    /// near-empty pools whose exchange rate was 2-100x away from the market — a single such
    /// dust print permanently became the bucket's High/Low, rendering every chart "stripy".
    ///
    /// Contract verified here: a trade's price may only enter an asset's USD series (and the
    /// pair series of two trusted assets) when it lies within a configurable band around that
    /// asset's cached PriceUSD (AppConfiguration.OhlcTrustedPriceBandFactor, default 1.5x).
    /// BiatecAsset.PriceUSD is depth-selected and continuously updated for every asset, trusted
    /// or not (see AggregatedPoolRepository.SelectAssetUsdPrice), so once it's established it is
    /// an authoritative reference regardless of trusted status - a print far outside the band is
    /// stale/thin-pool noise, not price discovery.
    ///
    /// 2026-08-15 incident: FOLKS (3203964481, NOT a trusted reference asset - one of ~100 pools
    /// with real ~$35k/24h combined volume) charted persistently "stripy" candles because the
    /// guard originally only applied to trusted assets; ANY of FOLKS' thin pools could write an
    /// unguarded print the moment its counter leg was trusted. Untrusted assets are now guarded
    /// exactly like trusted ones ONCE they have an established (&gt;0) cached price - an asset
    /// with no price yet remains unguarded (first price discovery via a trusted anchor still
    /// works - see OHLCRepositoryTrustedAnchorTests' bootstrap-case tests).
    /// </summary>
    public class OHLCRepositoryPriceBandTests
    {
        private const ulong Usdc = 31566704UL;      // default UsdReferenceAssetId, trusted
        private const ulong Algo = 0UL;             // always trusted
        private const ulong GoBtc = 386192725UL;    // in default TrustedReferenceAssetIds
        private const ulong Folks = 3203964481UL;   // NOT trusted, but has an established price

        private MockAssetRepository _assets = null!;
        private OHLCRepository _repo = null!;

        [SetUp]
        public async Task SetUp()
        {
            _assets = new MockAssetRepository();
            _repo = new OHLCRepository(null!, null!, _assets, Options.Create(new AppConfiguration()));

            await SetAssetAsync(Algo, decimals: 6, priceUsd: 0.09m);
            await SetAssetAsync(Usdc, decimals: 6, priceUsd: 1m);
            await SetAssetAsync(GoBtc, decimals: 8, priceUsd: 63000m);
            await SetAssetAsync(Folks, decimals: 6, priceUsd: 2m);
        }

        private async Task SetAssetAsync(ulong assetId, int decimals, decimal priceUsd)
        {
            var asset = await _assets.GetAssetAsync(assetId);
            asset!.Params.Decimals = (ulong)decimals;
            asset.PriceUSD = priceUsd;
            await _assets.SetAssetAsync(asset);
        }

        private static Trade MakeTrade(ulong assetIn, ulong amountIn, ulong assetOut, ulong amountOut) => new()
        {
            AssetIdIn = assetIn,
            AssetIdOut = assetOut,
            AssetAmountIn = amountIn,
            AssetAmountOut = amountOut,
            Timestamp = DateTimeOffset.Parse("2026-08-12T02:30:00Z"),
            TradeState = AVMTradeReporter.Models.Data.Enums.TxState.Confirmed
        };

        [Test]
        public async Task StaleTrustedPool_OffMarketPrint_WritesNoCandlesAtAll()
        {
            // The production ALGO case: 1 ALGO swapped for 9.15 USDC in a stale pool — a print
            // 100x away from ALGO's cached $0.09. It must not touch ALGO's USD series (would
            // chart H=$9.15), USDC's USD series (would chart $0.0098), or the ALGO/USDC pair
            // series (both sides trusted, the rate contradicts both cached prices).
            var trade = MakeTrade(Algo, 1_000_000, Usdc, 9_150_000);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            Assert.That(buckets, Is.Empty,
                "an off-market print between two trusted assets carries no usable price information");
        }

        [Test]
        public async Task InBandTrade_WritesAllThreeSeries()
        {
            // 100 ALGO ↔ 9 USDC at exactly the cached rate.
            var trade = MakeTrade(Algo, 100_000_000, Usdc, 9_000_000);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            var pair = buckets.Where(b => !b.InUsdValuation).ToList();
            var algoUsd = buckets.Where(b => b.InUsdValuation && b.AssetIdA == Algo).ToList();
            var usdcUsd = buckets.Where(b => b.InUsdValuation && b.AssetIdA == Usdc).ToList();

            Assert.That(pair.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(algoUsd.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(usdcUsd.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(algoUsd.All(b => b.Price == 0.09m));
            Assert.That(usdcUsd.All(b => b.Price == 1m));
        }

        [Test]
        public async Task GenuineMoveWithinBand_IsAccepted()
        {
            // 100 ALGO ↔ 12 USDC = $0.12, a genuine 33% move against the cached $0.09 — well
            // inside the 1.5x band, so all series must record it.
            var trade = MakeTrade(Algo, 100_000_000, Usdc, 12_000_000);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            Assert.That(buckets.Count, Is.EqualTo(3 * OHLCRepository.Intervals.Length));
            Assert.That(buckets.Where(b => b.InUsdValuation && b.AssetIdA == Algo).All(b => b.Price == 0.12m));
        }

        [Test]
        public async Task UntrustedAssetWithEstablishedPrice_IsNowGuardedToo()
        {
            // FOLKS cached at $2, but the trusted ALGO leg implies $40.50 (450 ALGO × $0.09 for
            // 1 FOLKS) - a >20x move, the exact shape of the 2026-08-15 production incident (a
            // thin/dust FOLKS pool paired with a trusted asset, no protection at all). FOLKS'
            // own USD print must now be rejected, same as a trusted asset's would be; the raw
            // on-chain pair rate is still recorded (real trade data, not a USD valuation claim),
            // and ALGO's own series is untouched either way (FOLKS was never a trusted anchor).
            var trade = MakeTrade(Folks, 1_000_000, Algo, 450_000_000);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            Assert.That(buckets.Where(b => b.InUsdValuation && b.AssetIdA == Folks), Is.Empty,
                "$40.50 is 20x FOLKS' established $2 cached price - outside the band, so rejected");
            Assert.That(buckets.Where(b => !b.InUsdValuation).Count(), Is.EqualTo(OHLCRepository.Intervals.Length),
                "the pair series keeps the exact on-chain rate - it is not a USD valuation claim");
            Assert.That(buckets.Where(b => b.InUsdValuation && b.AssetIdA == Algo), Is.Empty,
                "ALGO's own series is still protected: FOLKS is not a trusted anchor");
        }

        [Test]
        public async Task UnpricedAsset_StillGetsFirstPriceFromTrustedAnchor_Unguarded()
        {
            // Same trade, but for a token with no established cached price yet (bootstrap case) -
            // must still be written unguarded, exactly like before this fix, so first price
            // discovery via a trusted anchor keeps working for brand new assets.
            const ulong newToken = 424242424UL;
            var trade = MakeTrade(newToken, 1_000_000, Algo, 450_000_000);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            var newTokenUsd = buckets.Where(b => b.InUsdValuation && b.AssetIdA == newToken).ToList();
            Assert.That(newTokenUsd.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(newTokenUsd.All(b => b.Price == 40.50m));
        }

        [Test]
        public async Task TrustedAssetWithoutCachedPrice_IsNotGuarded()
        {
            // A trusted asset whose cached price is missing/zero cannot be band-checked — the
            // trusted-anchor rule alone applies (goBTC side is written from the USDC leg).
            await SetAssetAsync(GoBtc, decimals: 8, priceUsd: 0m);
            var trade = MakeTrade(GoBtc, 100_000, Usdc, 63_000_000);

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();

            var goBtcUsd = buckets.Where(b => b.InUsdValuation && b.AssetIdA == GoBtc).ToList();
            Assert.That(goBtcUsd.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(goBtcUsd.All(b => b.Price == 63000m));
        }

        [Test]
        public async Task BandDisabled_AcceptsOffMarketPrint()
        {
            var config = new AppConfiguration { OhlcTrustedPriceBandFactor = 0m };
            var repo = new OHLCRepository(null!, null!, _assets, Options.Create(config));
            var trade = MakeTrade(Algo, 1_000_000, Usdc, 9_150_000);

            var buckets = (await repo.GetIntervalBuckets(trade)).ToList();

            Assert.That(buckets.Where(b => b.InUsdValuation && b.AssetIdA == Algo).All(b => b.Price == 9.15m));
            Assert.That(buckets.Count, Is.EqualTo(3 * OHLCRepository.Intervals.Length));
        }
    }
}
