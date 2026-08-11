using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AVMTradeReporterTests.Repository
{
    /// <summary>
    /// Bucket-generation mechanics of <see cref="OHLCRepository.GetIntervalBuckets"/>: interval
    /// fan-out, pair canonicalization, doc ids and volumes. USD series follow the trusted-anchor
    /// rule (a leg is priced from its counter leg only when the counter is a trusted reference
    /// asset) — the full trust matrix is covered in <see cref="OHLCRepositoryTrustedAnchorTests"/>;
    /// here the trades are paired with the USD reference asset (always a $1 anchor, even without
    /// an asset repository) so USD buckets exist without further setup.
    /// </summary>
    public class OHLCRepositoryTests
    {
        private const ulong UsdcAssetId = 31566704UL;

        private OHLCRepository _repo = new(null, null);

        [Test]
        public async Task GetIntervalBuckets_BasicTrade_GeneratesAllBuckets()
        {
            // Sell 100 of asset 1 for 250 USDC (decimals 0 without an asset repository).
            var trade = new Trade
            {
                AssetIdIn = 1,
                AssetIdOut = UsdcAssetId,
                AssetAmountIn = 100,
                AssetAmountOut = 250,
                Timestamp = DateTimeOffset.Parse("2024-01-02T03:04:05Z"),
                TradeState = AVMTradeReporter.Models.Data.Enums.TxState.Confirmed
            };

            var buckets = await _repo.GetIntervalBuckets(trade);
            // 1 asset-valuation bucket + 1 usd-valuation bucket (only asset 1's leg has a trusted
            // counter — USDC's own leg has untrusted asset 1 as counter) per interval.
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
                Assert.That(b.DocId, Does.StartWith($"1-{UsdcAssetId}-{b.Interval}-asset-"));
            }

            foreach (var b in usdSeries)
            {
                // asset 1 anchored to the USDC leg: 250 USDC * $1 / 100 = $2.5
                Assert.That(b.AssetIdA, Is.EqualTo(1UL));
                Assert.That(b.Price, Is.EqualTo(2.5m));
                Assert.That(b.VolumeBase, Is.EqualTo(100m));
                Assert.That(b.VolumeQuote, Is.EqualTo(250m));
                Assert.That(b.AssetIdB, Is.EqualTo(UsdcAssetId));
                Assert.That(b.DocId, Does.StartWith($"1-{UsdcAssetId}-{b.Interval}-usd-"));
            }
        }

        [Test]
        public async Task GetIntervalBuckets_ReversedTrade_Canonicalizes()
        {
            // Same market as above but traded in the opposite direction: buy 120 of asset 1 for
            // 300 USDC. The canonical (base, quote) pair and prices must come out identical.
            var trade = new Trade
            {
                AssetIdIn = UsdcAssetId,
                AssetIdOut = 1,
                AssetAmountIn = 300,
                AssetAmountOut = 120,
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
            Assert.That(assetSeries.All(b => b.AssetIdB == UsdcAssetId));

            // asset 1 anchored to the USDC leg: 300 USDC * $1 / 120 = $2.5
            Assert.That(usdSeries.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(usdSeries.All(b => b.AssetIdA == 1UL));
            Assert.That(usdSeries.All(b => b.Price == 2.5m));
            Assert.That(usdSeries.All(b => b.AssetIdB == UsdcAssetId));
        }

        [Test]
        public async Task GetIntervalBuckets_TradeInvolvingUsdReferenceAsset_PricesTheOtherLegAtOneDollarAnchor()
        {
            // Trade of USDC against a newer asset (id > USDC id): sell 100 USDC for 50 X.
            var trade = new Trade
            {
                AssetIdIn = UsdcAssetId,
                AssetIdOut = 99999999UL,
                AssetAmountIn = 100,
                AssetAmountOut = 50,
                Timestamp = DateTimeOffset.Parse("2024-01-02T03:04:05Z"),
                TradeState = AVMTradeReporter.Models.Data.Enums.TxState.Confirmed
            };

            var buckets = (await _repo.GetIntervalBuckets(trade)).ToList();
            var usdSeries = buckets.Where(b => b.InUsdValuation).ToList();

            // X priced in USD from the USDC anchor: 100 USDC * $1 / 50 X = 2; stored as (X, USDC)
            // even though X id > USDC id.
            var otherUsd = usdSeries.Where(b => b.AssetIdA == 99999999UL).ToList();
            Assert.That(otherUsd.Count, Is.EqualTo(OHLCRepository.Intervals.Length));
            Assert.That(otherUsd.All(b => b.Price == 2m));
            Assert.That(otherUsd.All(b => b.AssetIdB == UsdcAssetId));

            // USDC's own USD series needs a TRUSTED counter as anchor; untrusted X cannot price it.
            // (See OHLCRepositoryTrustedAnchorTests.UsdReferenceVsTrusted_WritesTheUsdReferencesOwnSeries.)
            Assert.That(usdSeries.Where(b => b.AssetIdA == UsdcAssetId), Is.Empty);
        }

        [Test]
        public async Task GetTicks_CollapsesIntervalsIntoOneTickPerSeries()
        {
            var trade = new Trade
            {
                AssetIdIn = 1,
                AssetIdOut = UsdcAssetId,
                AssetAmountIn = 100,
                AssetAmountOut = 250,
                Timestamp = DateTimeOffset.Parse("2024-01-02T03:04:05Z"),
                TradeState = AVMTradeReporter.Models.Data.Enums.TxState.Confirmed
            };

            var buckets = await _repo.GetIntervalBuckets(trade);
            var ticks = OHLCRepository.GetTicks(buckets, trade.Timestamp!.Value);

            // one asset-valuation tick + one usd tick for the leg with a trusted counter
            Assert.That(ticks.Count, Is.EqualTo(2));
            Assert.That(ticks.All(t => t.Timestamp == trade.Timestamp.Value.ToUnixTimeSeconds()));

            var assetTick = ticks.Single(t => !t.InUSDValuation);
            Assert.That(assetTick.AssetIdA, Is.EqualTo(1UL));
            Assert.That(assetTick.AssetIdB, Is.EqualTo(UsdcAssetId));
            Assert.That(assetTick.Price, Is.EqualTo(2.5m));
            Assert.That(assetTick.VolumeBase, Is.EqualTo(100m));
            Assert.That(assetTick.VolumeQuote, Is.EqualTo(250m));

            var usdTick = ticks.Single(t => t.InUSDValuation);
            Assert.That(usdTick.AssetIdA, Is.EqualTo(1UL));
            Assert.That(usdTick.AssetIdB, Is.EqualTo(UsdcAssetId));
            Assert.That(usdTick.Price, Is.EqualTo(2.5m));
            Assert.That(usdTick.VolumeBase, Is.EqualTo(100m));
            Assert.That(usdTick.VolumeQuote, Is.EqualTo(250m));
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
