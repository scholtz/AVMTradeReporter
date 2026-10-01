using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using System.Globalization;

namespace AVMTradeReporter.Services.CoinGecko
{
    /// <summary>
    /// What the event mapper needs to know about one pool (the "pair"). Immutable facts only: GeckoTerminal queries
    /// the pair once and requires <c>asset0</c>/<c>asset1</c> never to swap places, so they follow the on-chain
    /// order of the pool (asset A = asset0, asset B = asset1).
    /// </summary>
    public sealed record PairInfo(
        ulong AppId,
        DEXProtocol Protocol,
        ulong Asset0Id,
        ulong Asset1Id,
        int Decimals0,
        int Decimals1,
        decimal? LpFee)
    {
        /// <summary>
        /// Biatec contracts keep the pool reserves in a fixed 1e9 scale whatever the decimals of the assets;
        /// the other protocols keep them in the asset's base units.
        /// </summary>
        public int ReserveDecimals0 => Protocol == DEXProtocol.Biatec ? 9 : Decimals0;
        public int ReserveDecimals1 => Protocol == DEXProtocol.Biatec ? 9 : Decimals1;
    }

    /// <summary>Block-local position of an event, assigned by <see cref="CoinGeckoEventOrdering"/>.</summary>
    public readonly record struct EventPositionPair(ulong TxnIndex, uint EventIndex);

    /// <summary>
    /// Pure conversion of stored <see cref="Trade"/> / <see cref="Liquidity"/> documents to the GeckoTerminal
    /// event schema. Returns null for anything that cannot be expressed safely: GeckoTerminal halts indexing of the
    /// whole DEX on an invalid event, so an unmappable event is skipped (and counted by the caller), never guessed.
    /// </summary>
    public static class CoinGeckoMapper
    {
        private static readonly decimal[] Pow10 = BuildPow10();

        private static decimal[] BuildPow10()
        {
            var table = new decimal[29];
            table[0] = 1m;
            for (var i = 1; i < table.Length; i++) table[i] = table[i - 1] * 10m;
            return table;
        }

        /// <summary><c>amount / 10^decimals</c>. Decimals above 28 cannot be expressed by <see cref="decimal"/> and yield null.</summary>
        public static decimal? Decimalize(ulong amount, int decimals)
        {
            if (decimals < 0 || decimals >= Pow10.Length) return null;
            return new decimal(amount) / Pow10[decimals];
        }

        /// <summary>Plain (never exponent) invariant formatting without trailing zeros: 1.50 -> "1.5", 0 -> "0".</summary>
        public static string Format(decimal value)
        {
            var text = value.ToString("0.############################", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        public static CoinGeckoSwapEvent? TryMapSwap(Trade trade, PairInfo pair, EventPositionPair position)
        {
            if (trade.Timestamp == null || trade.AssetAmountIn == 0 || trade.AssetAmountOut == 0) return null;

            bool zeroToOne;
            if (trade.AssetIdIn == pair.Asset0Id && trade.AssetIdOut == pair.Asset1Id) zeroToOne = true;
            else if (trade.AssetIdIn == pair.Asset1Id && trade.AssetIdOut == pair.Asset0Id) zeroToOne = false;
            else return null;

            var decIn = zeroToOne ? pair.Decimals0 : pair.Decimals1;
            var decOut = zeroToOne ? pair.Decimals1 : pair.Decimals0;
            var amountIn = Decimalize(trade.AssetAmountIn, decIn);
            var amountOut = Decimalize(trade.AssetAmountOut, decOut);
            var reserves = MapReserves(trade.A, trade.B, pair);
            if (amountIn is null || amountOut is null || reserves is null) return null;

            // asset0 quoted in asset1 = (asset1 amount) / (asset0 amount) of this swap
            var price = zeroToOne ? amountOut.Value / amountIn.Value : amountIn.Value / amountOut.Value;
            if (price <= 0) return null; // rounded to 0 by decimal precision - GeckoTerminal rejects a 0 price

            var swap = new CoinGeckoSwapEvent
            {
                Block = new CoinGeckoBlock { BlockNumber = trade.BlockId, BlockTimestamp = trade.Timestamp.Value.ToUnixTimeSeconds() },
                TxnId = string.IsNullOrEmpty(trade.TopTxId) ? trade.TxId : trade.TopTxId,
                TxnIndex = position.TxnIndex,
                EventIndex = position.EventIndex,
                Maker = trade.Trader,
                PairId = pair.AppId.ToString(CultureInfo.InvariantCulture),
                Reserves = reserves,
                PriceNative = Format(price),
            };
            // `assetIn` is what the trader paid (the LP fee stays in the pool and is reported as feesIn), `assetOut`
            // is what the trader received - the same convention as Uniswap v2's amountIn / amountOut.
            if (zeroToOne)
            {
                swap.Asset0In = Format(amountIn.Value);
                swap.Asset1Out = Format(amountOut.Value);
            }
            else
            {
                swap.Asset1In = Format(amountIn.Value);
                swap.Asset0Out = Format(amountOut.Value);
            }
            if (pair.LpFee is > 0 and < 1)
            {
                var fee = Format(decimal.Round(amountIn.Value * pair.LpFee.Value, 27, MidpointRounding.AwayFromZero));
                swap.Metadata = zeroToOne ? new CoinGeckoSwapMetadata { Fees0In = fee } : new CoinGeckoSwapMetadata { Fees1In = fee };
            }
            if (string.IsNullOrEmpty(swap.Maker) || string.IsNullOrEmpty(swap.TxnId)) return null;
            return swap;
        }

        public static CoinGeckoJoinExitEvent? TryMapLiquidity(Liquidity liquidity, PairInfo pair, EventPositionPair position)
        {
            if (liquidity.Timestamp == null) return null;
            if (liquidity.AssetAmountA == 0 && liquidity.AssetAmountB == 0) return null;

            ulong amount0, amount1;
            if (liquidity.AssetIdA == pair.Asset0Id && liquidity.AssetIdB == pair.Asset1Id)
            {
                amount0 = liquidity.AssetAmountA;
                amount1 = liquidity.AssetAmountB;
            }
            else if (liquidity.AssetIdA == pair.Asset1Id && liquidity.AssetIdB == pair.Asset0Id)
            {
                amount0 = liquidity.AssetAmountB;
                amount1 = liquidity.AssetAmountA;
            }
            else return null;

            var dec0 = Decimalize(amount0, pair.Decimals0);
            var dec1 = Decimalize(amount1, pair.Decimals1);
            var reserves = MapReserves(liquidity.A, liquidity.B, pair);
            if (dec0 is null || dec1 is null || reserves is null) return null;

            var join = new CoinGeckoJoinExitEvent
            {
                EventType = liquidity.Direction == LiquidityDirection.DepositLiquidity ? "join" : "exit",
                Block = new CoinGeckoBlock { BlockNumber = liquidity.BlockId, BlockTimestamp = liquidity.Timestamp.Value.ToUnixTimeSeconds() },
                TxnId = string.IsNullOrEmpty(liquidity.TopTxId) ? liquidity.TxId : liquidity.TopTxId,
                TxnIndex = position.TxnIndex,
                EventIndex = position.EventIndex,
                Maker = liquidity.LiquidityProvider,
                PairId = pair.AppId.ToString(CultureInfo.InvariantCulture),
                Reserves = reserves,
                Amount0 = Format(dec0.Value),
                Amount1 = Format(dec1.Value),
            };
            if (string.IsNullOrEmpty(join.Maker) || string.IsNullOrEmpty(join.TxnId)) return null;
            return join;
        }

        private static CoinGeckoReserves? MapReserves(ulong a, ulong b, PairInfo pair)
        {
            var reserve0 = Decimalize(a, pair.ReserveDecimals0);
            var reserve1 = Decimalize(b, pair.ReserveDecimals1);
            if (reserve0 is null || reserve1 is null) return null;
            return new CoinGeckoReserves { Asset0 = Format(reserve0.Value), Asset1 = Format(reserve1.Value) };
        }
    }
}
