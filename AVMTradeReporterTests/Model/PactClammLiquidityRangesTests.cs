using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using Newtonsoft.Json.Linq;

namespace AVMTradeReporterTests.Model
{
    /// <summary>
    /// Liquidity ranges of the Pact CLAMM (tick based concentrated liquidity) pool, verified against real mainnet data of the
    /// ALGO/USDC pool 3762241535 (Data/pact-clamm-3762241535-ticks.json: its tick word boxes read from algod).
    ///
    /// Before this the backend reported ~6.9k USD TVL for the pool (event tracked reserves that missed every mainnet
    /// liquidity add / remove) while Pact reported 18.93k USD.
    /// </summary>
    public class PactClammLiquidityRangesTests
    {
        private const ulong TickSpacing = 60;
        private const ulong SqrtPriceAfterSwap = 5531894802404564755UL;   // current_price after swap 4RRJGO6F... (round 66846070)
        private const ulong SqrtPriceBeforeSwap = 5531961541914522362UL;  // current_price after the previous swap ECIVKUOT... (round 66845971)

        // mint C3OJVQPDSJWIHDLGOZSM3JUNI3CP42R2EVGHQP5W7E2TNRB45ZHQ (round 66846441, after the swap): its log carries
        // tick_low 1000375, tick_high 1000433, liquidity 147806338 and the deposited 37627392 uALGO + 4000000 uUSDC
        private const ulong MintTickLow = 1000375;
        private const ulong MintTickHigh = 1000433;
        private const ulong MintLiquidity = 147_806_338;

        private static List<(ulong Tick, ulong Liquidity)> LoadTicks()
        {
            var json = JObject.Parse(File.ReadAllText("Data/pact-clamm-3762241535-ticks.json"));
            var ticks = new List<(ulong Tick, ulong Liquidity)>();
            foreach (var word in (JObject)json["words"]!)
            {
                ticks.AddRange(TickMath.ParseTickWord(ulong.Parse(word.Key), Convert.FromHexString(word.Value!.ToString())));
            }
            return ticks;
        }

        private static AVMTradeReporter.Models.Data.Pool MakePool(List<PoolLiquidityRange>? ranges)
        {
            return new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "TEXF2ZPVYW55BBUKV35GCX5GC27J7FBINBYHWTKZ4RED3ZTP2SEM4EEBL4",
                PoolAppId = 3762241535,
                Protocol = DEXProtocol.Pact,
                AMMType = AMMType.TickBasedCLAMM,
                AssetIdA = 0,
                AssetADecimals = 6,
                AssetIdB = 31566704,
                AssetBDecimals = 6,
                TickSpacing = TickSpacing,
                LiquidityRanges = ranges,
            };
        }

        [Test]
        public void TicksMergeIntoContiguousRanges()
        {
            var ticks = LoadTicks();
            Assert.That(ticks, Has.Count.EqualTo(172));

            var ranges = TickMath.BuildRanges(ticks);

            Assert.That(ranges, Has.Count.EqualTo(50));
            Assert.That(ranges.First().TickLow, Is.EqualTo(1000265));
            Assert.That(ranges.Last().TickHigh, Is.EqualTo(1000436));
            // ordered, non overlapping, every tick covered exactly once with its own liquidity
            for (var i = 1; i < ranges.Count; i++)
            {
                Assert.That(ranges[i].TickLow, Is.GreaterThan(ranges[i - 1].TickHigh));
            }
            foreach (var (tick, liquidity) in ticks)
            {
                var covering = ranges.Single(r => r.TickLow <= tick && tick <= r.TickHigh);
                Assert.That(covering.Liquidity, Is.EqualTo(liquidity));
            }
            // ticks 1000268..1000335 share the same liquidity and collapse into one range
            Assert.That(ranges.Any(r => r.TickLow == 1000268 && r.TickHigh == 1000335 && r.Liquidity == 71212546747UL));
        }

        [Test]
        public void ReservesMatchPactTvl()
        {
            var ranges = TickMath.BuildRanges(LoadTicks());

            var (a, b, active) = TickMath.ApplyPrice(ranges, SqrtPriceAfterSwap, TickSpacing, TickMath.TickBase, 6, 6);

            var price = TickMath.SqrtPriceX64ToPrice(SqrtPriceAfterSwap, 6, 6);
            Assert.That(price, Is.EqualTo(0.0899308m).Within(0.0000001m));
            Assert.That(a / 1e6m, Is.EqualTo(169_922m).Within(1m));
            Assert.That(b / 1e6m, Is.EqualTo(3_656.49m).Within(0.01m));
            // Pact reports 18.93k USD TVL (USDC ~ 1 USD)
            var tvl = a / 1e6m * price + b / 1e6m;
            Assert.That(tvl, Is.EqualTo(18_937.7m).Within(1m));
            // the liquidity active at the current tick 1000401 (record 1 of word 1000004)
            Assert.That(active, Is.EqualTo(337_594_319_121UL));
        }

        [Test]
        public void RangeAmountsAndBoundsAreConsistent()
        {
            var ranges = TickMath.BuildRanges(LoadTicks());
            var (a, b, _) = TickMath.ApplyPrice(ranges, SqrtPriceAfterSwap, TickSpacing, TickMath.TickBase, 6, 6);
            var price = TickMath.SqrtPriceX64ToPrice(SqrtPriceAfterSwap, 6, 6);

            // the per-range amounts add up to the pool reserves (each range rounds independently)
            Assert.That((decimal)ranges.Sum(r => (decimal)r.A), Is.EqualTo((decimal)a).Within(ranges.Count));
            Assert.That((decimal)ranges.Sum(r => (decimal)r.B), Is.EqualTo((decimal)b).Within(ranges.Count));
            foreach (var range in ranges)
            {
                Assert.That(range.PMin, Is.LessThan(range.PMax));
                // ranges entirely above the price hold only A, entirely below only B
                if (range.PMin > price) Assert.That(range.B, Is.EqualTo(0), $"range {range.TickLow}");
                if (range.PMax < price) Assert.That(range.A, Is.EqualTo(0), $"range {range.TickLow}");
            }
            var activeRange = ranges.Single(r => r.PMin <= price && price <= r.PMax);
            Assert.That(activeRange.TickLow, Is.EqualTo(1000401));
            Assert.That(activeRange.A, Is.GreaterThan(0));
            Assert.That(activeRange.B, Is.GreaterThan(0));
            // the price of A in B decreases with the tick: lower ticks are the higher prices
            Assert.That(ranges.First().PMin, Is.GreaterThan(ranges.Last().PMax));
        }

        [Test]
        public void MintLiquidityReproducesDeposit()
        {
            // a single position of the C3OJVQPD mint over [tick_low, tick_high) - tick_high is exclusive
            var ranges = new List<PoolLiquidityRange>
            {
                new PoolLiquidityRange { TickLow = MintTickLow, TickHigh = MintTickHigh - 1, Liquidity = MintLiquidity }
            };

            var (a, b, _) = TickMath.ApplyPrice(ranges, SqrtPriceAfterSwap, TickSpacing, TickMath.TickBase, 6, 6);

            Assert.That((decimal)a, Is.EqualTo(37_627_392m).Within(2m));
            Assert.That((decimal)b, Is.EqualTo(4_000_000m).Within(2m));
        }

        [Test]
        public void SwapReplayMatchesTradedAmounts()
        {
            // rebuild the tick liquidity of the time of swap 4RRJGO6F6WGE3GDYKXVSHOMAZTMW6GJVRIZTEGIKTYREWOQL6A2Q by removing the
            // later C3OJVQPD mint, then compare the reserves before / after the swap with what the trader paid / received:
            // 13.6 ALGO in (0.3 % fee goes to the positions, not the tick liquidity), 1.219404 USDC out
            var ticks = LoadTicks()
                .Select(t => t.Tick >= MintTickLow && t.Tick < MintTickHigh ? (t.Tick, t.Liquidity - MintLiquidity) : t)
                .ToList();
            var ranges = TickMath.BuildRanges(ticks);

            var (aBefore, bBefore, _) = TickMath.ApplyPrice(ranges, SqrtPriceBeforeSwap, TickSpacing, TickMath.TickBase, 6, 6);
            var (aAfter, bAfter, _) = TickMath.ApplyPrice(ranges, SqrtPriceAfterSwap, TickSpacing, TickMath.TickBase, 6, 6);

            var deltaA = (decimal)aAfter - aBefore;
            var deltaB = (decimal)bBefore - bAfter;
            Assert.That(deltaA, Is.EqualTo(13_600_000m * 0.997m).Within(13_600_000m * 0.005m));
            Assert.That(deltaB, Is.EqualTo(1_219_404m).Within(1_219_404m * 0.005m));
        }

        [Test]
        public void SwapRecomputesReservesFromRanges()
        {
            var ranges = TickMath.BuildRanges(LoadTicks());
            var pool = MakePool(ranges);
            pool.A = 1; // stale event tracked values must be replaced, not adjusted
            pool.B = 1;

            PoolReserveUpdater.ApplyTrade(pool, new Trade
            {
                AssetIdIn = 0,
                AssetIdOut = 31566704,
                AssetAmountIn = 13_600_000,
                AssetAmountOut = 1_219_404,
                PoolSqrtPriceX64 = SqrtPriceAfterSwap,
            });

            var expected = TickMath.ApplyPrice(TickMath.BuildRanges(LoadTicks()), SqrtPriceAfterSwap, TickSpacing, TickMath.TickBase, 6, 6);
            Assert.That(pool.A, Is.EqualTo(expected.A));
            Assert.That(pool.B, Is.EqualTo(expected.B));
            Assert.That(pool.L, Is.EqualTo(expected.ActiveLiquidity));
            Assert.That(pool.CurrentPrice, Is.EqualTo(TickMath.SqrtPriceX64ToPrice(SqrtPriceAfterSwap, 6, 6)));
            // the repriced ranges are a new list: the previous one (possibly being serialized) is left untouched
            Assert.That(pool.LiquidityRanges, Is.Not.SameAs(ranges));
            Assert.That(TickMath.SameRanges(pool.LiquidityRanges, ranges), Is.True);
            Assert.That(pool.LiquidityRanges!.Single(r => r.TickLow == 1000401).A, Is.GreaterThan(0));
            Assert.That(ranges.All(r => r.A == 0 && r.B == 0 && r.PMin == 0 && r.PMax == 0), Is.True);
        }

        [Test]
        public void PriceOnRangeBoundaryBelongsToTheRangeStartingThere()
        {
            // the active tick 1000401 starts (lower sqrt price bound) at sqrt(1.0001^(-30 * 402)); a price exactly there is
            // inside tick 1000401, not in a gap between ranges
            var ranges = TickMath.BuildRanges(LoadTicks());
            var lowerBound = TickMath.TickBounds(1000402, TickSpacing).Upper;
            // at ~5.5e18 a double is integer valued, so the Q64.64 value converts back to exactly the bound
            var sqrtPriceX64 = (ulong)(lowerBound * 18446744073709551616.0);
            Assert.That(sqrtPriceX64 / 18446744073709551616.0, Is.EqualTo(lowerBound));

            var (_, _, active) = TickMath.ApplyPrice(ranges, sqrtPriceX64, TickSpacing, TickMath.TickBase, 6, 6);

            Assert.That(active, Is.EqualTo(337_594_319_121UL));
        }

        [Test]
        public void ReverseMapsUnboundedBoundsOntoEachOther()
        {
            var range = new PoolLiquidityRange { PMin = 0, PMax = decimal.MaxValue, A = 1, B = 2 };

            var reversed = range.Reverse();

            Assert.That(reversed.PMin, Is.EqualTo(0));
            Assert.That(reversed.PMax, Is.EqualTo(decimal.MaxValue));
            Assert.That(reversed.A, Is.EqualTo(2));
            Assert.That(reversed.B, Is.EqualTo(1));
            Assert.That(new PoolLiquidityRange { PMin = 0, PMax = 4 }.Reverse().PMax, Is.EqualTo(decimal.MaxValue));
            Assert.That(new PoolLiquidityRange { PMin = 0, PMax = 4 }.Reverse().PMin, Is.EqualTo(0.25m));
            // a range clamped entirely below (both bounds 0) is entirely above once reversed, not the whole price axis
            var degenerate = new PoolLiquidityRange { PMin = 0, PMax = 0 }.Reverse();
            Assert.That(degenerate.PMin, Is.EqualTo(decimal.MaxValue));
            Assert.That(degenerate.PMax, Is.EqualTo(decimal.MaxValue));
        }

        [Test]
        public void ActiveLiquidityFollowsTheContractTick()
        {
            // the price says tick 1000401, but the contract's current_tick is authoritative (e.g. on a tick boundary)
            var pool = MakePool(TickMath.BuildRanges(LoadTicks()));
            pool.CurrentTick = 1000400;

            PoolReserveUpdater.ApplyTrade(pool, new Trade { AssetIdIn = 0, AssetIdOut = 31566704, AssetAmountIn = 10, AssetAmountOut = 1, PoolSqrtPriceX64 = SqrtPriceAfterSwap });
            Assert.That(pool.L, Is.EqualTo(TickMath.LiquidityAtTick(pool.LiquidityRanges!, 1000400)));
            Assert.That(pool.L, Is.Not.EqualTo(337_594_319_121UL));

            // a trade that moved the tick carries it in the global delta
            PoolReserveUpdater.ApplyTrade(pool, new Trade { AssetIdIn = 0, AssetIdOut = 31566704, AssetAmountIn = 10, AssetAmountOut = 1, PoolSqrtPriceX64 = SqrtPriceAfterSwap, PoolTick = 1000401 });
            Assert.That(pool.L, Is.EqualTo(337_594_319_121UL));
        }

        [Test]
        public void SwapWithoutPriceChangeKeepsReserves()
        {
            var pool = MakePool(TickMath.BuildRanges(LoadTicks()));
            pool.A = 123;
            pool.B = 456;

            PoolReserveUpdater.ApplyTrade(pool, new Trade { AssetIdIn = 0, AssetIdOut = 31566704, AssetAmountIn = 10, AssetAmountOut = 1 });

            Assert.That(pool.A, Is.EqualTo(123));
            Assert.That(pool.B, Is.EqualTo(456));
        }

        [Test]
        public void LiquidityEventDropsStaleRangesAndFallsBackToDeltas()
        {
            var pool = MakePool(TickMath.BuildRanges(LoadTicks()));
            pool.A = 1_000_000;
            pool.B = 2_000_000;

            PoolReserveUpdater.ApplyLiquidity(pool, new Liquidity
            {
                Direction = LiquidityDirection.DepositLiquidity,
                AssetIdA = 0,
                AssetIdB = 31566704,
                AssetAmountA = 37_627_392,
                AssetAmountB = 4_000_000,
            });

            Assert.That(pool.LiquidityRanges, Is.Null);
            Assert.That(pool.A, Is.EqualTo(1_000_000 + 37_627_392));
            Assert.That(pool.B, Is.EqualTo(2_000_000 + 4_000_000));

            // without ranges the swap falls back to the incremental tracking
            PoolReserveUpdater.ApplyTrade(pool, new Trade { AssetIdIn = 0, AssetIdOut = 31566704, AssetAmountIn = 10, AssetAmountOut = 1, PoolSqrtPriceX64 = SqrtPriceAfterSwap });
            Assert.That(pool.A, Is.EqualTo(1_000_000 + 37_627_392 + 10));
            Assert.That(pool.B, Is.EqualTo(2_000_000 + 4_000_000 - 1));
        }

        [Test]
        public void ReverseMirrorsRanges()
        {
            var pool = MakePool(TickMath.BuildRanges(LoadTicks()));
            PoolReserveUpdater.ApplyRanges(pool, SqrtPriceAfterSwap);
            pool.CurrentPrice = TickMath.SqrtPriceX64ToPrice(SqrtPriceAfterSwap, 6, 6);

            var reversed = pool.Reverse();

            Assert.That(reversed.LiquidityRanges, Has.Count.EqualTo(pool.LiquidityRanges!.Count));
            for (var i = 0; i < pool.LiquidityRanges.Count; i++)
            {
                var original = pool.LiquidityRanges[i];
                var mirrored = reversed.LiquidityRanges![i];
                Assert.That(mirrored.TickLow, Is.EqualTo(original.TickLow));
                Assert.That(mirrored.TickHigh, Is.EqualTo(original.TickHigh));
                Assert.That(mirrored.Liquidity, Is.EqualTo(original.Liquidity));
                Assert.That(mirrored.A, Is.EqualTo(original.B));
                Assert.That(mirrored.B, Is.EqualTo(original.A));
                Assert.That(mirrored.PMin, Is.EqualTo(1 / original.PMax).Within(0.0000001m));
                Assert.That(mirrored.PMax, Is.EqualTo(1 / original.PMin).Within(0.0000001m));
            }
            // reversing must not mutate the original ranges
            Assert.That(reversed.LiquidityRanges, Is.Not.SameAs(pool.LiquidityRanges));
        }

        [Test]
        public void SameRangesComparesBoundsAndLiquidityOnly()
        {
            var left = TickMath.BuildRanges(LoadTicks());
            var right = TickMath.BuildRanges(LoadTicks());
            TickMath.ApplyPrice(left, SqrtPriceAfterSwap, TickSpacing, TickMath.TickBase, 6, 6);
            TickMath.ApplyPrice(right, SqrtPriceBeforeSwap, TickSpacing, TickMath.TickBase, 6, 6);

            Assert.That(TickMath.SameRanges(left, right), Is.True);
            Assert.That(TickMath.SameRanges(null, null), Is.True);
            Assert.That(TickMath.SameRanges(left, null), Is.False);
            right[3].Liquidity++;
            Assert.That(TickMath.SameRanges(left, right), Is.False);
        }

        [Test]
        public void TickRecordAboveUint64IsRejected()
        {
            var word = new byte[TickMath.TickRecordSize * TickMath.TicksPerWord];
            word[TickMath.TickRecordSize * 5 + 1] = 1; // tick 5 holds 2^64

            Assert.Throws<OverflowException>(() => TickMath.ParseTickWord(TickMath.TickBase, word).ToList());
        }
    
        [Test]
        public void TickOriginDecidesTheOrientationOfTheSqrtPrice()
        {
            // USDC/POW 3764749314 around its crossing of price 1 (round 66736741: tick 1000000, d = 1e6; round 66737071: tick 78, d = 0)
            var pool = new AVMTradeReporter.Models.Data.Pool
            {
                PoolAddress = "2M75ASPFJ5YWJOYU676IOL3NX23H6R333RWX4WONRALEWTJA5FQWDOH4GQ",
                Protocol = DEXProtocol.Pact,
                AMMType = AMMType.TickBasedCLAMM,
                AssetADecimals = 6,
                AssetBDecimals = 6,
                TickSpacing = 200,
                TickOrigin = TickMath.TickBase,
            };
            PoolReserveUpdater.ApplyTrade(pool, new Trade { PoolSqrtPriceX64 = 18324627607556582812UL, PoolTick = 1000000 });
            Assert.That(pool.CurrentPrice, Is.EqualTo(0.98675m).Within(0.0001m)); // sqrt(price A in B) * 2^64

            PoolReserveUpdater.ApplyTrade(pool, new Trade { PoolSqrtPriceX64 = 8395360813952946615UL, PoolTick = 78, PoolTickOrigin = 0 });

            Assert.That(pool.TickOrigin, Is.EqualTo(0));
            // now sqrt(price B in A) * 2^64: the price keeps rising past 1 to ~4.83, inside tick 78 = 1.0001^(200 * [78, 79])
            Assert.That(pool.CurrentPrice, Is.EqualTo(4.8279m).Within(0.001m));
            var (lower, upper) = TickMath.TickBounds(78, 200);
            Assert.That((double)pool.CurrentPrice!.Value, Is.InRange(lower * lower, upper * upper));
        }

        [Test]
        public void TickRegionsMapToSignedTicks()
        {
            // words below 1e6 hold ticks w * 100 + i, words from 1e6 on 1e6 + (w - 1e6) * 100 + i
            Assert.That(TickMath.WordToTick(4, 33), Is.EqualTo(433));
            Assert.That(TickMath.WordToTick(1000004, 1), Is.EqualTo(1000401));
            // tick 1e6 covers prices [1.0001^-spacing, 1], tick 0 covers [1, 1.0001^spacing]: they meet at price 1
            Assert.That(TickMath.TickBounds(1000000, 60).Upper, Is.EqualTo(1d));
            Assert.That(TickMath.TickBounds(0, 60).Lower, Is.EqualTo(1d));
            Assert.That(TickMath.TickBounds(0, 60).Upper, Is.EqualTo(Math.Pow(1.0001, 30)).Within(1e-12));
            // and are never merged into one range although their encodings are adjacent to 999999
            var ranges = TickMath.BuildRanges(new[] { (999_999UL, 5UL), (1_000_000UL, 5UL), (1_000_001UL, 5UL) });
            Assert.That(ranges, Has.Count.EqualTo(2));
            Assert.That(ranges[1].TickLow, Is.EqualTo(1_000_000));
            Assert.That(ranges[1].TickHigh, Is.EqualTo(1_000_001));
        }

        [Test]
        public void ScamRatedPoolExposesNoRangeBalances()
        {
            var pool = MakePool(TickMath.BuildRanges(LoadTicks()));
            pool.ScamRating = 100;

            AVMTradeReporter.Services.ScamRating.ScamRatingPolicy.ApplyBalanceRule(pool);

            Assert.That(pool.LiquidityRanges, Is.Null);
        }
}
}
