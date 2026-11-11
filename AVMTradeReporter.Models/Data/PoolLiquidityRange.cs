namespace AVMTradeReporter.Models.Data
{
    /// <summary>
    /// One contiguous run of ticks with the same liquidity of a tick based CLAMM pool (Pact CLAMM), loaded from the pool's
    /// tick word boxes.
    ///
    /// Each range behaves exactly like a standalone concentrated liquidity position over [PMin, PMax] with liquidity
    /// <see cref="Liquidity"/>: the pool is the sum of its ranges, so the pool reserves are the sum of the ranges' A / B
    /// at the current price. Swaps never change the per-tick liquidity, only the price, so the reserves can be recomputed
    /// exactly after every swap from the post-swap sqrt price; only liquidity add / remove changes the ranges (and triggers
    /// a reload from the boxes).
    /// </summary>
    public class PoolLiquidityRange
    {
        /// <summary>
        /// First contract tick of the range (inclusive). Raw contract value, unaffected by <see cref="Pool.Reverse"/>.
        /// </summary>
        public ulong TickLow { get; set; }

        /// <summary>
        /// Last contract tick of the range (inclusive). Raw contract value, unaffected by <see cref="Pool.Reverse"/>.
        /// </summary>
        public ulong TickHigh { get; set; }

        /// <summary>
        /// Liquidity (sqrt of base units) active in every tick of the range.
        /// </summary>
        public ulong Liquidity { get; set; }

        /// <summary>
        /// Lowest price of 1 A in B (decimals adjusted) covered by the range.
        /// </summary>
        public decimal PMin { get; set; }

        /// <summary>
        /// Highest price of 1 A in B (decimals adjusted) covered by the range.
        /// </summary>
        public decimal PMax { get; set; }

        /// <summary>
        /// Amount of asset A (base units) held by the range at the current price.
        /// </summary>
        public ulong A { get; set; }

        /// <summary>
        /// Amount of asset B (base units) held by the range at the current price.
        /// </summary>
        public ulong B { get; set; }

        public PoolLiquidityRange Reverse()
        {
            return new PoolLiquidityRange
            {
                TickLow = TickLow,
                TickHigh = TickHigh,
                Liquidity = Liquidity,
                PMin = PMax == 0 ? 0 : 1 / PMax,
                PMax = PMin == 0 ? 0 : 1 / PMin,
                A = B,
                B = A,
            };
        }
    }
}
