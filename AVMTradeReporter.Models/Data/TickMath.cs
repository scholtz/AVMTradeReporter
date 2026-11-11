namespace AVMTradeReporter.Models.Data
{
    /// <summary>
    /// Math for tick based concentrated liquidity pools (Pact CLAMM).
    ///
    /// Conventions (verified against the testnet pool 763274703 and the mainnet pools 3762241535, 3764749314, 3764898004
    /// and 3764935215, whose box-derived TVL equals the TVL of the Pact API to the cent):
    /// - ticks are expressed in tick_spacing units and encoded sign-magnitude around <see cref="TickBase"/> (1_000_000):
    ///   an encoded tick t >= 1e6 is the signed tick s = t - 1e6 and covers s in [s, s + 1]; t &lt; 1e6 is s = -t and
    ///   covers s in [-(t + 1), -t]. The price of A in B (base units) at signed tick s is 1.0001^(-spacing * s), so
    ///   ticks >= 1e6 are prices &lt;= 1 and ticks &lt; 1e6 prices >= 1; tick 0 and tick 1e6 meet at price 1
    /// - the global "d" (<see cref="Pool.TickOrigin"/>) tells on which side the current price is: 1e6 while the price of
    ///   A in B is &lt;= 1, 0 above (a swap crossing price 1 flips it, e.g. 3764749314 in round 66737071)
    /// - current_price is Q64.64 sqrt price in the orientation of that side: sqrt(price of A in B) * 2^64 while d = 1e6,
    ///   sqrt(price of B in A) * 2^64 while d = 0 (see <see cref="CurrentSqrtPrice"/>)
    /// - liquidity per tick is stored in boxes named by an 8 byte "word" w, 100 ticks per word, in the same two regions:
    ///   w >= 1e6 holds ticks 1e6 + (w - 1e6) * 100 + i, w &lt; 1e6 holds ticks w * 100 + i
    ///   as 100 records of 10 bytes: big endian uint80 liquidity active in that tick (the mint logs carry the position
    ///   liquidity as uint80 too; the two high bytes are 0 in every record of both the testnet and the mainnet pool)
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
        public static decimal SqrtPriceX64ToPrice(ulong sqrtPriceX64, ulong assetADecimals, ulong assetBDecimals, ulong tickOrigin = TickBase)
        {
            if (sqrtPriceX64 == 0) return 0;
            var sqrt = CurrentSqrtPrice(sqrtPriceX64, tickOrigin);
            var priceBaseUnits = sqrt * sqrt;
            var price = priceBaseUnits * Math.Pow(10, (double)assetADecimals - (double)assetBDecimals);
            if (double.IsNaN(price) || double.IsInfinity(price)) return 0;
            return Convert.ToDecimal(price);
        }

        /// <summary>
        /// Converts a tick word (box name) and the record index to the tick.
        /// </summary>
        public static ulong WordToTick(ulong word, int index)
        {
            if (word >= TickBase) return TickBase + (word - TickBase) * (ulong)TicksPerWord + (ulong)index;
            return word * (ulong)TicksPerWord + (ulong)index;
        }

        /// <summary>
        /// sqrt of the price of A in B (base units) at the lower and upper boundary of the encoded tick.
        /// </summary>
        public static (double Lower, double Upper) TickBounds(ulong tick, ulong tickSpacing)
        {
            double AtSigned(double s) => Math.Pow(1.0001, -(double)tickSpacing / 2 * s);
            if (tick >= TickBase)
            {
                var s = (double)(tick - TickBase);
                return (AtSigned(s + 1), AtSigned(s));
            }
            return (AtSigned(-(double)tick), AtSigned(-((double)tick + 1)));
        }

        /// <summary>
        /// sqrt of the price of A in B (base units) from the contract current_price, whose orientation follows the
        /// tick origin "d" (<see cref="Pool.TickOrigin"/>): A in B while d >= 1e6, B in A while d = 0.
        /// </summary>
        public static double CurrentSqrtPrice(ulong sqrtPriceX64, ulong tickOrigin)
        {
            if (sqrtPriceX64 == 0) return 0;
            return tickOrigin >= TickBase ? sqrtPriceX64 / TwoPow64 : TwoPow64 / sqrtPriceX64;
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
        /// Liquidity of the range containing the tick (0 when no range does). With the contract's current_tick this is the
        /// liquidity active at the current price - the contract's own answer, also on a tick boundary.
        /// </summary>
        public static ulong LiquidityAtTick(IEnumerable<PoolLiquidityRange> ranges, ulong tick)
        {
            return ranges.FirstOrDefault(r => r.TickLow <= tick && tick <= r.TickHigh)?.Liquidity ?? 0;
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
                // tick 1e6 - 1 and tick 1e6 are not adjacent prices (the two ends of the encoding), never merge across them
                if (current != null && current.Liquidity == liquidity && current.TickHigh + 1 == tick && tick != TickBase)
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
        /// A range never spans the sign boundary (see <see cref="BuildRanges"/>), so its sqrt price interval is spanned by
        /// the bounds of its first and last tick (<see cref="TickBounds"/>). Below the interval the range holds only A,
        /// above it only B, inside both:
        /// A = L * (1/sqrtP - 1/sqrtUpper), B = L * (sqrtP - sqrtLower) - the standard concentrated liquidity formula.
        /// </summary>
        public static (ulong A, ulong B, ulong ActiveLiquidity) ApplyPrice(List<PoolLiquidityRange> ranges, ulong sqrtPriceX64, ulong tickSpacing, ulong tickOrigin, ulong assetADecimals, ulong assetBDecimals)
        {
            var sqrtP = CurrentSqrtPrice(sqrtPriceX64, tickOrigin);
            var decimalsShift = Math.Pow(10, (double)assetADecimals - (double)assetBDecimals);
            double totalA = 0, totalB = 0;
            ulong active = 0;
            foreach (var range in ranges)
            {
                var low = TickBounds(range.TickLow, tickSpacing);
                var high = TickBounds(range.TickHigh, tickSpacing);
                var sa = Math.Min(low.Lower, high.Lower); // lower price boundary
                var sb = Math.Max(low.Upper, high.Upper); // upper price boundary
                double a, b;
                // a tick covers [lower, upper): a price exactly on a boundary belongs to the range starting there
                if (sqrtP < sa)
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
        /// Copies the ranges, so a reprice never mutates a list another thread may be serializing.
        /// </summary>
        public static List<PoolLiquidityRange> CloneRanges(IEnumerable<PoolLiquidityRange> ranges)
        {
            return ranges.Select(r => r.Clone()).ToList();
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
