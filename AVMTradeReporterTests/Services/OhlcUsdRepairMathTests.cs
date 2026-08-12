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
        public void ForwardFillCloses_FillsRequestedKeysWithLastKnownClose()
        {
            // Bucket keys are bucket-start unix seconds, so the fill works for ANY interval —
            // including weeks and calendar months, where buckets are not arithmetically spaced.
            long Key(string iso) => DateTimeOffset.Parse(iso).ToUnixTimeSeconds();
            var k0 = Key("2026-08-11T10:00:00Z");
            var k1 = Key("2026-08-11T11:00:00Z");
            var k2 = Key("2026-08-11T12:00:00Z");
            var k3 = Key("2026-08-11T13:00:00Z");
            var candles = new Dictionary<long, OHLC>
            {
                [k0] = AssetCandle(0UL, Usdc, 0.09m, 0.09m, 0.09m, 0.09m, 1m, 1m),
                [k3] = AssetCandle(0UL, Usdc, 0.08m, 0.08m, 0.08m, 0.08m, 1m, 1m),
            };

            var closes = OhlcUsdRepairService.ForwardFillCloses(candles, new[] { k0 - 3600, k0, k1, k2, k3, k3 + 3600 });

            Assert.That(closes.ContainsKey(k0 - 3600), Is.False, "no anchor exists before the first candle");
            Assert.That(closes[k0], Is.EqualTo(0.09m));
            Assert.That(closes[k1], Is.EqualTo(0.09m), "gap buckets carry the last close forward");
            Assert.That(closes[k2], Is.EqualTo(0.09m));
            Assert.That(closes[k3], Is.EqualTo(0.08m));
            Assert.That(closes[k3 + 3600], Is.EqualTo(0.08m), "fill extends past the last candle");
        }

        [Test]
        public void ForwardFillCloses_EmptyInput_ReturnsEmpty()
        {
            var closes = OhlcUsdRepairService.ForwardFillCloses(new Dictionary<long, OHLC>(), new[] { 1L, 2L });
            Assert.That(closes, Is.Empty);
        }

        [Test]
        public void WickBand_ClampsOutlierHighAndLow_ToBandAroundBody()
        {
            // Historical pair candles can carry a handful of genuine off-market dust prints in
            // their High/Low (a stale ALGO/USDC pool printed 9.11 while the candle body sat at
            // ~0.093). The rebuild clamps wicks into ±band around the candle body (open/close)
            // so those prints cannot resurface in the rebuilt USD series.
            var candle = AssetCandle(5UL, Usdc, o: 2m, h: 30m, l: 0.1m, c: 2.5m, volBase: 100m, volQuote: 250m);

            var usd = OhlcUsdRepairMath.RebuildUsdCandle(5UL, Usdc, candle, anchorUsdPrice: 1m, Updated, wickBandFactor: 1.5m);

            Assert.That(usd, Is.Not.Null);
            Assert.That(usd!.Open, Is.EqualTo(2m));
            Assert.That(usd.Close, Is.EqualTo(2.5m));
            Assert.That(usd.High, Is.EqualTo(2.5m * 1.5m), "high clamped to max(open, close) × band");
            Assert.That(usd.Low, Is.EqualTo(2m / 1.5m), "low clamped to min(open, close) / band");
        }

        [Test]
        public void WickBand_LeavesInBandWicksUntouched_OnBothPaths()
        {
            var direct = AssetCandle(5UL, Usdc, o: 2m, h: 2.6m, l: 1.8m, c: 2.5m, volBase: 100m, volQuote: 250m);
            var usdDirect = OhlcUsdRepairMath.RebuildUsdCandle(5UL, Usdc, direct, 1m, Updated, wickBandFactor: 1.5m);
            Assert.That(usdDirect!.High, Is.EqualTo(2.6m));
            Assert.That(usdDirect.Low, Is.EqualTo(1.8m));

            // Inverted path: stored (USDC, X) candle with an absurd stored high → X's low after
            // inversion; the clamp must apply to the REBUILT orientation.
            const ulong X = 999999999UL;
            var inverted = AssetCandle(Usdc, X, o: 4m, h: 400m, l: 2m, c: 2.5m, volBase: 1000m, volQuote: 3000m);
            var usdInv = OhlcUsdRepairMath.RebuildUsdCandle(X, Usdc, inverted, 1m, Updated, wickBandFactor: 1.5m);
            // Rebuilt body: open 0.25, close 0.4 → low bound 0.25/1.5; stored high 400 inverts
            // to low 0.0025, far below the bound.
            Assert.That(usdInv!.High, Is.EqualTo(0.5m), "in-band high stays");
            Assert.That(usdInv.Low, Is.EqualTo(0.25m / 1.5m), "outlier low clamped after inversion");
        }

        [Test]
        public void WickBand_ZeroFactor_DisablesClamping()
        {
            var candle = AssetCandle(5UL, Usdc, o: 2m, h: 30m, l: 0.1m, c: 2.5m, volBase: 100m, volQuote: 250m);
            var usd = OhlcUsdRepairMath.RebuildUsdCandle(5UL, Usdc, candle, 1m, Updated, wickBandFactor: 0m);
            Assert.That(usd!.High, Is.EqualTo(30m));
            Assert.That(usd.Low, Is.EqualTo(0.1m));
        }

        [Test]
        public void RepairPlan_CoversEveryStoredInterval_NotJustOneHour()
        {
            // The 2026-08-11 repair only rebuilt 1h candles: every other interval (1m…1M) kept
            // serving ValueUSD-polluted candles to the charts — the 4h chart stayed "stripy".
            var config = new AVMTradeReporter.Model.Configuration.OhlcRepairConfiguration();

            var plan = OhlcUsdRepairService.GetRepairPlan(config);

            var expected = AVMTradeReporter.Repository.OHLCRepository.Intervals.Select(i => i.code).ToList();
            Assert.That(plan.Select(p => p.Interval), Is.EquivalentTo(expected));

            foreach (var step in plan)
            {
                var isFine = step.Interval is "1m" or "5m" or "15m";
                Assert.That(step.Window, Is.EqualTo(TimeSpan.FromDays(isFine ? config.FineWindowDays : config.CoarseWindowDays)),
                    $"interval {step.Interval} must use the {(isFine ? "fine" : "coarse")} rebuild window");
            }
            Assert.That(config.CoarseWindowDays, Is.GreaterThanOrEqualTo(220),
                "USD candles have existed since 2026-01-17 — the coarse window must reach back over the whole polluted span");
        }
    }
}
