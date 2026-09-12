using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;

namespace AVMTradeReporterTests.Model
{
    /// <summary>
    /// Tick math of the Pact CLAMM pools, verified against on-chain data of the testnet pool 763274703.
    /// </summary>
    public class TickMathTests
    {
        private const ulong Spacing = 60;

        [Test]
        public void CurrentTickRangeContainsCurrentPrice()
        {
            // global state after the swap in round 64139970: current_tick 1000361, current_price 6237834869436999446
            var sqrtP = 6237834869436999446UL / 18446744073709551616.0;
            var lower = TickMath.TickToSqrtPrice(1000362, Spacing);
            var upper = TickMath.TickToSqrtPrice(1000361, Spacing);
            Assert.That(sqrtP, Is.GreaterThan(lower));
            Assert.That(sqrtP, Is.LessThan(upper));
            // price decreases with the tick
            Assert.That(TickMath.TickToSqrtPrice(1000000, Spacing), Is.EqualTo(1.0).Within(1e-12));
        }

        [Test]
        public void LowAndHighPriceGlobalStateMatchTickBoundaries()
        {
            // global state after the swap in round 64139970: current_tick 1000361,
            // low_price 6246004806130590512 and high_price 6227295804679904691 are the Q64.64 sqrt prices
            // at the boundaries of the current tick (named from the contract's A-per-B view, so "low" is the larger sqrt(B/A))
            const double twoPow64 = 18446744073709551616.0;
            var low = TickMath.TickToSqrtPrice(1000361, Spacing) * twoPow64;
            var high = TickMath.TickToSqrtPrice(1000362, Spacing) * twoPow64;
            Assert.That(low, Is.EqualTo(6246004806130590512.0).Within(0.000000001).Percent);
            Assert.That(high, Is.EqualTo(6227295804679904691.0).Within(0.000000001).Percent);
            // current_price sits inside the current tick
            Assert.That(6237834869436999446.0, Is.LessThan(low));
            Assert.That(6237834869436999446.0, Is.GreaterThan(high));
        }

        [Test]
        public void WordAndIndexMapToTick()
        {
            // box 0x0f4243 (1000003) record 31 is tick 1000331 (first tick of the big position 1000331..1000396)
            Assert.That(TickMath.WordToTick(1000003, 31), Is.EqualTo(1000331));
            Assert.That(TickMath.WordToTick(1000003, 61), Is.EqualTo(1000361));
        }

        [Test]
        public void ParseTickWordReadsTenByteRecords()
        {
            var box = new byte[1000];
            // record 31: uint16 flags + uint64 liquidity 433647667 (0x19d8f033)
            var offset = 31 * TickMath.TickRecordSize + 2;
            var value = 433647667UL;
            for (var j = 7; j >= 0; j--) { box[offset + j] = (byte)(value & 0xff); value >>= 8; }
            var ticks = TickMath.ParseTickWord(1000003, box).ToList();
            Assert.That(ticks.Count, Is.EqualTo(1));
            Assert.That(ticks[0].Tick, Is.EqualTo(1000331));
            Assert.That(ticks[0].Liquidity, Is.EqualTo(433647667));
        }

        [Test]
        public void ReservesOfThePositionsMatchTheMintedAmounts()
        {
            // two mints into ticks [1000331, 1000396) - upper tick exclusive - (rounds 64139870 and 64139914) while current_price was 6261539103397907236:
            // 35682498 + 71364996 ALGO and 4999999 + 9999999 USDC. The tick boxes show liquidity 433647667 in every tick of the range.
            var ticks = Enumerable.Range(1000331, 65).Select(t => ((ulong)t, 433647667UL)).ToList();
            var (a, b) = TickMath.ComputeReserves(ticks, 6261539103397907236UL, Spacing);
            Assert.That(a, Is.EqualTo(35682498UL + 71364996UL).Within(2000));
            Assert.That(b, Is.EqualTo(4999999UL + 9999999UL).Within(200));
        }

        [Test]
        public void ReservesMoveWithThePrice()
        {
            var ticks = Enumerable.Range(1000331, 66).Select(t => ((ulong)t, 433647667UL)).ToList();
            // price above the range (lower tick) -> everything in B; below the range -> everything in A
            var (aAbove, bAbove) = TickMath.ComputeReserves(ticks, (ulong)(TickMath.TickToSqrtPrice(1000300, Spacing) * 18446744073709551616.0), Spacing);
            Assert.That(aAbove, Is.EqualTo(0));
            Assert.That(bAbove, Is.GreaterThan(0));
            var (aBelow, bBelow) = TickMath.ComputeReserves(ticks, (ulong)(TickMath.TickToSqrtPrice(1000400, Spacing) * 18446744073709551616.0), Spacing);
            Assert.That(aBelow, Is.GreaterThan(0));
            Assert.That(bBelow, Is.EqualTo(0));
            Assert.That(TickMath.ActiveLiquidity(ticks, 1000361), Is.EqualTo(433647667));
            Assert.That(TickMath.ActiveLiquidity(ticks, 1000500), Is.EqualTo(0));
        }

        [Test]
        public void TradeUpdatesPoolPriceAndTick()
        {
            var pool = new AVMTradeReporter.Models.Data.Pool
            {
                Protocol = DEXProtocol.Pact,
                AMMType = AMMType.TickBasedCLAMM,
                AssetIdA = 0,
                AssetADecimals = 6,
                AssetIdB = 10458941,
                AssetBDecimals = 6,
                A = 100_000_000,
                B = 15_000_000,
                CurrentPrice = 0.1160m,
                CurrentTick = 1000358,
            };
            PoolReserveUpdater.ApplyTrade(pool, new Trade
            {
                AssetIdIn = 0,
                AssetAmountIn = 5000000,
                AssetIdOut = 10458941,
                AssetAmountOut = 572191,
                PoolSqrtPriceX64 = 6237834869436999446UL,
                PoolTick = 1000361,
            });
            Assert.That(pool.A, Is.EqualTo(105_000_000));
            Assert.That(pool.B, Is.EqualTo(15_000_000 - 572191));
            Assert.That(pool.CurrentPrice, Is.EqualTo(0.1143479m).Within(0.0000001m));
            Assert.That(pool.CurrentTick, Is.EqualTo(1000361));
            var implied = pool.VirtualAmountBForPrice / pool.VirtualAmountAForPrice;
            Assert.That(implied, Is.EqualTo(0.1143479m).Within(0.000001m));
        }
    }
}
