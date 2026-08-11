using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Services;

namespace AVMTradeReporterTests.Services
{
    /// <summary>
    /// Rebuild math for the one-shot USD OHLC repair: historical `-usd-` 1h candles were polluted
    /// by ValueUSD averaging (see OHLCRepositoryTrustedAnchorTests), but the `-asset-` candles are
    /// exact on-chain exchange rates and were never polluted. A USD candle for asset X is rebuilt
    /// from the X↔anchor asset candle and the anchor's USD price: identity/scale when X is the
    /// stored base, inversion (with high/low swap) when X is the stored quote.
    /// </summary>
    public class OhlcUsdRepairMathTests
    {
        private const ulong Usdc = 31566704UL;
        private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-08T02:00:00Z");
        private static readonly DateTimeOffset Updated = DateTimeOffset.Parse("2026-08-11T22:00:00Z");

        private static OHLC AssetCandle(ulong a, ulong b, decimal o, decimal h, decimal l, decimal c, decimal volBase, decimal volQuote, long trades = 7) => new()
        {
            AssetIdA = a,
            AssetIdB = b,
            Interval = "1h",
            InUSDValuation = false,
            StartTime = Start,
            Open = o,
            High = h,
            Low = l,
            Close = c,
            VolumeBase = volBase,
            VolumeQuote = volQuote,
            Trades = trades
        };

        [Test]
        public void AssetIsBase_DirectUsdPair_ScalesByOneDollarAnchor()
        {
            // Pair (X=5, USDC): stored price is already USDC per X.
            var candle = AssetCandle(5UL, Usdc, o: 2m, h: 3m, l: 1m, c: 2.5m, volBase: 100m, volQuote: 250m);

            var usd = OhlcUsdRepairMath.RebuildUsdCandle(5UL, Usdc, candle, anchorUsdPrice: 1m, Updated);

            Assert.That(usd, Is.Not.Null);
            Assert.That(usd!.AssetIdA, Is.EqualTo(5UL));
            Assert.That(usd.AssetIdB, Is.EqualTo(Usdc));
            Assert.That(usd.InUSDValuation, Is.True);
            Assert.That(usd.Interval, Is.EqualTo("1h"));
            Assert.That(usd.StartTime, Is.EqualTo(Start));
            Assert.That(usd.Open, Is.EqualTo(2m));
            Assert.That(usd.High, Is.EqualTo(3m));
            Assert.That(usd.Low, Is.EqualTo(1m));
            Assert.That(usd.Close, Is.EqualTo(2.5m));
            Assert.That(usd.VolumeBase, Is.EqualTo(100m), "base volume stays in X units");
            Assert.That(usd.VolumeQuote, Is.EqualTo(250m), "quote volume is volumeBase × close in USD");
            Assert.That(usd.Trades, Is.EqualTo(7));
            Assert.That(usd.LastUpdated, Is.EqualTo(Updated));
            Assert.That(usd.Id, Is.EqualTo($"5-{Usdc}-1h-usd-20260808020000"));
        }

        [Test]
        public void AssetIsQuote_InvertsRate_AndSwapsHighLow()
        {
            // Pair (USDC, X) with X id > USDC id: stored price is X per USDC, so X's USD price is
            // the inverse — and the stored LOW of X-per-USDC is X's HIGH in USD.
            const ulong X = 999999999UL;
            var candle = AssetCandle(Usdc, X, o: 4m, h: 5m, l: 2m, c: 2.5m, volBase: 1000m, volQuote: 3000m);

            var usd = OhlcUsdRepairMath.RebuildUsdCandle(X, Usdc, candle, anchorUsdPrice: 1m, Updated);

            Assert.That(usd, Is.Not.Null);
            Assert.That(usd!.AssetIdA, Is.EqualTo(X));
            Assert.That(usd.AssetIdB, Is.EqualTo(Usdc));
            Assert.That(usd.Open, Is.EqualTo(0.25m));
            Assert.That(usd.High, Is.EqualTo(0.5m), "high = anchor / stored low");
            Assert.That(usd.Low, Is.EqualTo(0.2m), "low = anchor / stored high");
            Assert.That(usd.Close, Is.EqualTo(0.4m));
            Assert.That(usd.VolumeBase, Is.EqualTo(3000m), "X was the stored quote, so its volume is volumeQuote");
            Assert.That(usd.VolumeQuote, Is.EqualTo(3000m * 0.4m));
        }

        [Test]
        public void AssetIsQuoteOfAlgoPair_ScalesByAlgoUsdPrice()
        {
            // Pair (ALGO=0, X): stored price is X per ALGO; anchor is ALGO's USD price that hour.
            const ulong X = 1284444444UL;
            var candle = AssetCandle(0UL, X, o: 10m, h: 20m, l: 8m, c: 16m, volBase: 500m, volQuote: 6000m);

            var usd = OhlcUsdRepairMath.RebuildUsdCandle(X, Usdc, candle, anchorUsdPrice: 0.08m, Updated);

            Assert.That(usd, Is.Not.Null);
            Assert.That(usd!.Open, Is.EqualTo(0.008m));
            Assert.That(usd.High, Is.EqualTo(0.01m), "high = algoUsd / stored low");
            Assert.That(usd.Low, Is.EqualTo(0.004m), "low = algoUsd / stored high");
            Assert.That(usd.Close, Is.EqualTo(0.005m));
            Assert.That(usd.VolumeBase, Is.EqualTo(6000m));
            Assert.That(usd.VolumeQuote, Is.EqualTo(6000m * 0.005m));
        }

        [Test]
        public void AlgoItself_IsBaseOfTheUsdReferencePair()
        {
            // ALGO's own USD series comes from the (0, USDC) pair with a $1 anchor.
            var candle = AssetCandle(0UL, Usdc, o: 0.09m, h: 0.095m, l: 0.088m, c: 0.09m, volBase: 12345m, volQuote: 1111m);

            var usd = OhlcUsdRepairMath.RebuildUsdCandle(0UL, Usdc, candle, anchorUsdPrice: 1m, Updated);

            Assert.That(usd, Is.Not.Null);
            Assert.That(usd!.AssetIdA, Is.EqualTo(0UL));
            Assert.That(usd.Open, Is.EqualTo(0.09m));
            Assert.That(usd.High, Is.EqualTo(0.095m));
            Assert.That(usd.Low, Is.EqualTo(0.088m));
            Assert.That(usd.Close, Is.EqualTo(0.09m));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void NonPositiveAnchor_ReturnsNull(int anchor)
        {
            var candle = AssetCandle(5UL, Usdc, 2m, 3m, 1m, 2.5m, 100m, 250m);
            Assert.That(OhlcUsdRepairMath.RebuildUsdCandle(5UL, Usdc, candle, anchor, Updated), Is.Null);
        }

        [Test]
        public void MissingOrNonPositiveOhlcComponent_ReturnsNull()
        {
            var missing = AssetCandle(5UL, Usdc, 2m, 3m, 1m, 2.5m, 100m, 250m);
            missing.High = null;
            Assert.That(OhlcUsdRepairMath.RebuildUsdCandle(5UL, Usdc, missing, 1m, Updated), Is.Null);

            var zero = AssetCandle(5UL, Usdc, 0m, 3m, 1m, 2.5m, 100m, 250m);
            Assert.That(OhlcUsdRepairMath.RebuildUsdCandle(5UL, Usdc, zero, 1m, Updated), Is.Null,
                "a zero component would divide by zero on the inverted path and is garbage on both");
        }

        [Test]
        public void AssetNotInPair_ReturnsNull()
        {
            var candle = AssetCandle(5UL, Usdc, 2m, 3m, 1m, 2.5m, 100m, 250m);
            Assert.That(OhlcUsdRepairMath.RebuildUsdCandle(77UL, Usdc, candle, 1m, Updated), Is.Null);
        }

        [Test]
        public void BuildForwardFilledCloses_FillsGapsWithLastKnownClose_UpToNow()
        {
            long Hour(string iso) => DateTimeOffset.Parse(iso).ToUnixTimeSeconds() / 3600;
            var h0 = Hour("2026-08-11T10:00:00Z");
            var candles = new Dictionary<long, OHLC>
            {
                [h0] = AssetCandle(0UL, Usdc, 0.09m, 0.09m, 0.09m, 0.09m, 1m, 1m),
                [h0 + 3] = AssetCandle(0UL, Usdc, 0.08m, 0.08m, 0.08m, 0.08m, 1m, 1m),
            };

            var closes = OhlcUsdRepairService.BuildForwardFilledCloses(candles, DateTimeOffset.Parse("2026-08-11T15:30:00Z"));

            Assert.That(closes[h0], Is.EqualTo(0.09m));
            Assert.That(closes[h0 + 1], Is.EqualTo(0.09m), "gap hours carry the last close forward");
            Assert.That(closes[h0 + 2], Is.EqualTo(0.09m));
            Assert.That(closes[h0 + 3], Is.EqualTo(0.08m));
            Assert.That(closes[h0 + 5], Is.EqualTo(0.08m), "fill extends to the current hour");
            Assert.That(closes.ContainsKey(h0 - 1), Is.False, "no anchor exists before the first candle");
            Assert.That(closes.ContainsKey(h0 + 6), Is.False, "fill stops at the current hour");
        }

        [Test]
        public void BuildForwardFilledCloses_EmptyInput_ReturnsEmpty()
        {
            var closes = OhlcUsdRepairService.BuildForwardFilledCloses(new Dictionary<long, OHLC>(), DateTimeOffset.UtcNow);
            Assert.That(closes, Is.Empty);
        }
    }
}
