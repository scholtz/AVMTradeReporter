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
    ///   as 100 records of 10 bytes: uint16 flags + uint64 liquidity active in that tick
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
            var sqrtP = sqrtPriceX64 / TwoPow64;
            double totalA = 0, totalB = 0;
            foreach (var (tick, liquidity) in ticks)
            {
                var sa = TickToSqrtPrice(tick + 1, tickSpacing); // lower price boundary
                var sb = TickToSqrtPrice(tick, tickSpacing);     // upper price boundary
                if (sqrtP <= sa)
                {
                    totalA += liquidity * (1 / sa - 1 / sb);
                }
                else if (sqrtP >= sb)
                {
                    totalB += liquidity * (sb - sa);
                }
                else
                {
                    totalA += liquidity * (1 / sqrtP - 1 / sb);
                    totalB += liquidity * (sqrtP - sa);
                }
            }
            return (ToUlong(totalA), ToUlong(totalB));
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

        private static ulong ToUlong(double value)
        {
            if (double.IsNaN(value) || value <= 0) return 0;
            if (value >= ulong.MaxValue) return ulong.MaxValue;
            return (ulong)Math.Round(value);
        }
    }
}
