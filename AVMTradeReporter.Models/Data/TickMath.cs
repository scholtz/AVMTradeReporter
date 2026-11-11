namespace AVMTradeReporter.Models.Data
{
    /// <summary>
    /// Math for tick based concentrated liquidity pools (Pact CLAMM).
    ///
    /// Conventions verified against the testnet pool 763274703:
    /// - the contract sqrt price is Q64.64: sqrt(price of A in B, base units) * 2^64
    /// - ticks are unsigned with 1_000_000 meaning "price 1" and are expressed in tick_spacing units,
    ///   so tick t covers the B/A price range [1.0001^(-spacing*(t+1-1e6)), 1.0001^(-spacing*(t-1e6))]
    ///   (the price of A in B decreases with the tick)
    /// - liquidity per tick is stored in boxes named by an 8 byte "word" w (100 ticks per word, tick = 1e6 + (w - 1e6) * 100 + i)
    ///   as 100 records of 10 bytes: big endian uint80 liquidity active in that tick
    /// - a position minted over (tick_low, tick_high) adds its liquidity to the ticks [tick_low, tick_high) - tick_high is
    ///   exclusive (verified on mainnet pool 3762241535: the C3OJVQPD... mint deposit matches the formula to 1 base unit)
    /// - verified on mainnet ALGO/USDC pool 3762241535 (contract of factory 3656084442): the reserves summed over the
    ///   tick boxes equal the TVL Pact reports for the pool
    /// </summary>
    public static class TickMath
    {
        public const ulong TickBase = 1_000_000;
        public const int TicksPerWord = 100;
        public const int TickRecordSize = 10;
        private const double TwoPow64 = 18446744073709551616.0;

        /// <summary>
        /// Converts the Q64.64 sqrt price to the decimal adjusted price of 1 A in B.
        /// </summary>
        public static decimal SqrtPriceX64ToPrice(ulong sqrtPriceX64, ulong assetADecimals, ulong assetBDecimals)
        {
            var sqrt = sqrtPriceX64 / TwoPow64;
            var priceBaseUnits = sqrt * sqrt;
            var price = priceBaseUnits * Math.Pow(10, (double)assetADecimals - (double)assetBDecimals);
            if (double.IsNaN(price) || double.IsInfinity(price)) return 0;
            return Convert.ToDecimal(price);
        }

        /// <summary>
        /// sqrt of the B/A price (base units) at the lower boundary of the tick.
        /// </summary>
        public static double TickToSqrtPrice(ulong tick, ulong tickSpacing)
        {
            return Math.Pow(1.0001, -(double)tickSpacing / 2 * ((double)tick - TickBase));
        }

        /// <summary>
        /// Converts a tick word (box name) and the record index to the tick.
        /// </summary>
        public static ulong WordToTick(ulong word, int index)
        {
            return TickBase + (word - TickBase) * (ulong)TicksPerWord + (ulong)index;
        }

        /// <summary>
        /// Parses the liquidity records of a tick word box into (tick, liquidity) pairs. Empty ticks are skipped.
        /// </summary>
        public static IEnumerable<(ulong Tick, ulong Liquidity)> ParseTickWord(ulong word, byte[] value)
        {
            if (value == null) yield break;
            for (var i = 0; i < TicksPerWord; i++)
            {
                var offset = i * TickRecordSize;
                if (offset + TickRecordSize > value.Length) yield break;
                // the record is a big endian uint80 (the mint / burn logs carry the position liquidity as uint80 too);
                // a value that does not fit uint64 cannot be represented - fail the read so the caller falls back
                if (value[offset] != 0 || value[offset + 1] != 0)
                {
                    throw new OverflowException($"Liquidity of tick {WordToTick(word, i)} exceeds uint64");
                }
                ulong liquidity = 0;
                for (var j = 2; j < TickRecordSize; j++)
                {
                    liquidity = (liquidity << 8) | value[offset + j];
                }
                if (liquidity == 0) continue;
                yield return (WordToTick(word, i), liquidity);
            }
        }

        /// <summary>
        /// Computes the amounts of A and B (base units) held by the given per-tick liquidity at the current sqrt price.
        /// </summary>
        public static (ulong A, ulong B) ComputeReserves(IEnumerable<(ulong Tick, ulong Liquidity)> ticks, ulong sqrtPriceX64, ulong tickSpacing)
        {
            var ranges = BuildRanges(ticks);
            var (a, b, _) = ApplyPrice(ranges, sqrtPriceX64, tickSpacing, 0, 0);
            return (a, b);
        }

        /// <summary>
        /// Liquidity active at the current tick.
        /// </summary>
        public static ulong ActiveLiquidity(IEnumerable<(ulong Tick, ulong Liquidity)> ticks, ulong currentTick)
        {
            foreach (var (tick, liquidity) in ticks)
            {
                if (tick == currentTick) return liquidity;
            }
            return 0;
        }

        /// <summary>
        /// Splits the per-tick liquidity into contiguous ranges of equal liquidity (adjacent ticks with the same liquidity
        /// are merged), ordered by tick. Each range acts as a standalone concentrated liquidity position, see
        /// <see cref="PoolLiquidityRange"/>. Amounts and prices are filled by <see cref="ApplyPrice"/>.
        /// </summary>
        public static List<PoolLiquidityRange> BuildRanges(IEnumerable<(ulong Tick, ulong Liquidity)> ticks)
        {
            var result = new List<PoolLiquidityRange>();
            PoolLiquidityRange? current = null;
            foreach (var (tick, liquidity) in ticks.Where(t => t.Liquidity > 0).OrderBy(t => t.Tick))
            {
                if (current != null && current.Liquidity == liquidity && current.TickHigh + 1 == tick)
                {
                    current.TickHigh = tick;
                    continue;
                }
                current = new PoolLiquidityRange { TickLow = tick, TickHigh = tick, Liquidity = liquidity };
                result.Add(current);
            }
            return result;
        }

        /// <summary>
        /// Fills the amounts held by every range at the given sqrt price (Q64.64) and the decimals adjusted price bounds,
        /// and returns the pool totals and the liquidity active at that price.
        ///
        /// The ticks of a range cover the sqrt price interval [sqrt(TickHigh + 1), sqrt(TickLow)] (the price of A in B
        /// decreases with the tick). Below the interval the range holds only A, above it only B, inside both:
        /// A = L * (1/sqrtP - 1/sqrtUpper), B = L * (sqrtP - sqrtLower) - the standard concentrated liquidity formula.
        /// </summary>
        public static (ulong A, ulong B, ulong ActiveLiquidity) ApplyPrice(List<PoolLiquidityRange> ranges, ulong sqrtPriceX64, ulong tickSpacing, ulong assetADecimals, ulong assetBDecimals)
        {
            var sqrtP = sqrtPriceX64 / TwoPow64;
            var decimalsShift = Math.Pow(10, (double)assetADecimals - (double)assetBDecimals);
            double totalA = 0, totalB = 0;
            ulong active = 0;
            foreach (var range in ranges)
            {
                var sa = TickToSqrtPrice(range.TickHigh + 1, tickSpacing); // lower price boundary
                var sb = TickToSqrtPrice(range.TickLow, tickSpacing);      // upper price boundary
                double a, b;
                if (sqrtP <= sa)
                {
                    a = range.Liquidity * (1 / sa - 1 / sb);
                    b = 0;
                }
                else if (sqrtP >= sb)
                {
                    a = 0;
                    b = range.Liquidity * (sb - sa);
                }
                else
                {
                    a = range.Liquidity * (1 / sqrtP - 1 / sb);
                    b = range.Liquidity * (sqrtP - sa);
                    active = range.Liquidity;
                }
                range.A = ToUlong(a);
                range.B = ToUlong(b);
                range.PMin = ToDecimalPrice(sa * sa * decimalsShift);
                range.PMax = ToDecimalPrice(sb * sb * decimalsShift);
                totalA += a;
                totalB += b;
            }
            return (ToUlong(totalA), ToUlong(totalB), active);
        }

        /// <summary>
        /// Same ranges (tick bounds and liquidity) - amounts and prices are derived and ignored.
        /// </summary>
        public static bool SameRanges(IReadOnlyList<PoolLiquidityRange>? left, IReadOnlyList<PoolLiquidityRange>? right)
        {
            if (left == null || right == null) return left == null && right == null;
            if (left.Count != right.Count) return false;
            for (var i = 0; i < left.Count; i++)
            {
                if (left[i].TickLow != right[i].TickLow || left[i].TickHigh != right[i].TickHigh || left[i].Liquidity != right[i].Liquidity) return false;
            }
            return true;
        }

        /// <summary>
        /// Converts the decimals adjusted price to decimal; prices outside of the decimal range (extreme ticks) are clamped,
        /// 0 meaning "unbounded below" and <see cref="decimal.MaxValue"/> "unbounded above".
        /// </summary>
        private static decimal ToDecimalPrice(double price)
        {
            if (double.IsNaN(price) || price <= 1e-28) return 0;
            if (double.IsInfinity(price) || price >= (double)decimal.MaxValue) return decimal.MaxValue;
            return Convert.ToDecimal(price);
        }

        private static ulong ToUlong(double value)
        {
            if (double.IsNaN(value) || value <= 0) return 0;
            if (value >= ulong.MaxValue) return ulong.MaxValue;
            return (ulong)Math.Round(value);
        }
    }
}
