using AVMTradeReporter.Models.Data.Enums;

namespace AVMTradeReporter.Models.Data
{
    /// <summary>
    /// Applies trade / liquidity events to the pool reserves.
    ///
    /// Classic AMMs report the absolute reserves (A, B, L) in every event and the pool is simply overwritten.
    /// Tick based CLAMM pools (Pact CLAMM) do not expose reserves in their state, so the events carry A = B = 0
    /// and the reserves are tracked incrementally from the swapped / deposited / withdrawn amounts.
    /// </summary>
    public static class PoolReserveUpdater
    {
        public static bool IsDeltaTracked(Pool pool, ulong eventA, ulong eventB)
        {
            return pool.AMMType == AMMType.TickBasedCLAMM || (eventA == 0 && eventB == 0);
        }

        public static void ApplyTrade(Pool pool, Trade trade)
        {
            pool.MarkEventApplied(liquidityEvent: false);
            if (pool.AMMType == AMMType.TickBasedCLAMM && pool.LiquidityRanges is { } loadedRanges && trade.A == 0 && trade.B == 0)
            {
                // a swap moves only the price, never the per-tick liquidity: the reserves follow exactly from the loaded
                // liquidity ranges at the post-swap price. A swap that left the price untouched (no current_price in the
                // global delta - Algorand lists only the keys whose value changed) left the reserves untouched too.
                // The price state goes first: a swap crossing price 1 flips the tick origin, which decides how the sqrt price
                // is read, and the new tick decides the active liquidity.
                ApplyPrice(pool, trade);
                if (trade.PoolSqrtPriceX64.HasValue && trade.PoolSqrtPriceX64.Value > 0)
                {
                    ApplyRanges(pool, trade.PoolSqrtPriceX64.Value);
                }
                else if (pool.CurrentTick.HasValue)
                {
                    pool.L = TickMath.LiquidityAtTick(pool.LiquidityRanges ?? loadedRanges, pool.CurrentTick.Value);
                }
                return;
            }
            if (!IsDeltaTracked(pool, trade.A, trade.B))
            {
                pool.A = trade.A;
                pool.B = trade.B;
                if (trade.L > 0) pool.L = trade.L;
                return;
            }
            if (trade.A > 0 || trade.B > 0)
            {
                pool.A = trade.A;
                pool.B = trade.B;
                if (trade.L > 0) pool.L = trade.L;
                return;
            }
            Add(pool, trade.AssetIdIn, trade.AssetAmountIn);
            Subtract(pool, trade.AssetIdOut, trade.AssetAmountOut);
            ApplyPrice(pool, trade);
        }

        /// <summary>
        /// Recomputes the reserves (A, B), the active liquidity (L) and the per-range amounts of a tick based CLAMM pool
        /// from its liquidity ranges at the given sqrt price (Q64.64), read in the orientation of <see cref="Pool.TickOrigin"/>.
        /// </summary>
        public static void ApplyRanges(Pool pool, ulong sqrtPriceX64)
        {
            // reprice a copy and swap the reference: the cached pool's list may be serialized concurrently (ES / Redis /
            // SignalR). A reload that replaced the list meanwhile is repriced in turn (the price is this swap's); ranges a
            // liquidity event invalidated are never put back - its reload reprices them.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var current = pool.LiquidityRanges;
                if (current == null) return;
                var ranges = TickMath.CloneRanges(current);
                var (a, b, active) = TickMath.Reprice(ranges, sqrtPriceX64, pool.TickSpacing ?? 1, pool.EffectiveTickOrigin, pool.CurrentTick, pool.AssetADecimals ?? 0, pool.AssetBDecimals ?? 0);
                if (!pool.TryReplaceLiquidityRanges(current, ranges)) continue;
                pool.A = a;
                pool.B = b;
                pool.L = active;
                return;
            }
        }

        /// <summary>
        /// Updates the pool price / tick from the post-swap state carried by the trade (tick based CLAMM pools).
        /// </summary>
        public static void ApplyPrice(Pool pool, Trade trade)
        {
            if (trade.PoolTickOrigin.HasValue)
            {
                pool.TickOrigin = trade.PoolTickOrigin.Value;
            }
            else if (trade.PoolTick.HasValue && pool.AMMType == AMMType.TickBasedCLAMM)
            {
                // the side of price 1 follows from the tick itself (ticks >= 1e6 are prices <= 1): a swap that moved the tick
                // pins its own orientation, even when it is applied against a pool state read later (e.g. after a reload)
                pool.TickOrigin = trade.PoolTick.Value >= TickMath.TickBase ? TickMath.TickBase : 0;
            }
            if (trade.PoolSqrtPriceX64.HasValue && trade.PoolSqrtPriceX64.Value > 0)
            {
                pool.SqrtPriceX64 = trade.PoolSqrtPriceX64.Value;
                pool.CurrentPrice = TickMath.SqrtPriceX64ToPrice(trade.PoolSqrtPriceX64.Value, pool.AssetADecimals ?? 0, pool.AssetBDecimals ?? 0, pool.EffectiveTickOrigin);
            }
            if (trade.PoolTick.HasValue)
            {
                pool.CurrentTick = trade.PoolTick.Value;
            }
        }

        /// <summary>
        /// The per-tick liquidity of a tick based CLAMM pool changed (a liquidity event): drop its ranges, so that neither
        /// a swap nor a chain read started before puts them back (<see cref="Pool.TryReplaceLiquidityRanges"/>), until
        /// they are reloaded from the box storage.
        /// </summary>
        public static void InvalidateRanges(Pool pool)
        {
            pool.MarkEventApplied(liquidityEvent: true);
            pool.LiquidityRanges = null;
        }

        public static void ApplyLiquidity(Pool pool, Liquidity liquidity)
        {
            if (pool.AMMType == AMMType.TickBasedCLAMM)
            {
                // the event changed the per-tick liquidity, so the loaded ranges are stale: fall back to the incremental
                // tracking below until the ranges are reloaded from the box storage (PoolRepository reloads them right away)
                InvalidateRanges(pool);
            }
            else
            {
                pool.MarkEventApplied(liquidityEvent: true);
            }
            if (!IsDeltaTracked(pool, liquidity.A, liquidity.B))
            {
                pool.A = liquidity.A;
                pool.B = liquidity.B;
                if (liquidity.L > 0) pool.L = liquidity.L;
                return;
            }
            if (liquidity.A > 0 || liquidity.B > 0)
            {
                pool.A = liquidity.A;
                pool.B = liquidity.B;
                if (liquidity.L > 0) pool.L = liquidity.L;
                return;
            }
            if (liquidity.Direction == LiquidityDirection.DepositLiquidity)
            {
                Add(pool, liquidity.AssetIdA, liquidity.AssetAmountA);
                Add(pool, liquidity.AssetIdB, liquidity.AssetAmountB);
            }
            else
            {
                Subtract(pool, liquidity.AssetIdA, liquidity.AssetAmountA);
                Subtract(pool, liquidity.AssetIdB, liquidity.AssetAmountB);
            }
        }

        private static void Add(Pool pool, ulong assetId, ulong amount)
        {
            if (amount == 0) return;
            if (pool.AssetIdA.HasValue && pool.AssetIdA.Value == assetId) pool.A = (pool.A ?? 0) + amount;
            else if (pool.AssetIdB.HasValue && pool.AssetIdB.Value == assetId) pool.B = (pool.B ?? 0) + amount;
        }

        private static void Subtract(Pool pool, ulong assetId, ulong amount)
        {
            if (amount == 0) return;
            if (pool.AssetIdA.HasValue && pool.AssetIdA.Value == assetId) pool.A = (pool.A ?? 0) > amount ? pool.A - amount : 0;
            else if (pool.AssetIdB.HasValue && pool.AssetIdB.Value == assetId) pool.B = (pool.B ?? 0) > amount ? pool.B - amount : 0;
        }
    }
}
