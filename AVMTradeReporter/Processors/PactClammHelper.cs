using Algorand;
using Algorand.Algod.Model.Transactions;

namespace AVMTradeReporter.Processors
{
    /// <summary>
    /// Shared helpers for the Pact CLAMM (tick based concentrated liquidity) pool contracts (testnet example app 763274703).
    ///
    /// Contract layout:
    /// - ARC4 methods: swap(txn,uint64)void, mint(uint64,uint64,txn,txn)void (tick_low, tick_high), burn(uint64,uint64)void
    /// - global state: a / b = asset ids, current_price = sqrt(price B/A in base units) * 2^64, current_tick, tick_spacing,
    ///   fee_bps, protocol_fee_bps (portion of the fee in bps), vault / vault_id = shared vault application holding the funds
    /// - no reserves and no LP token: positions are boxes; funds are held by the vault (same vault ABI as the weighted pools)
    /// - the pool calls the vault as an inner transaction: deposit(asset_a, amt_a, asset_b, amt_b, ...),
    ///   swap(asset_in, amt_in, asset_out, amt_out, fee, ..., recipient), withdraw(asset_a, amt_a, asset_b, amt_b, ..., recipient);
    ///   outgoing transfers are nested inner transactions of the vault call
    /// </summary>
    public static class PactClammHelper
    {
        public const string SwapSelector = "4336f62a"; // swap(txn,uint64)void
        public const string MintSelector = "0ab9446c"; // mint(uint64,uint64,txn,txn)void
        public const string BurnSelector = "db75d435"; // burn(uint64,uint64)void

        public const string VaultDepositSelector = "7da0eaa3";
        public const string VaultSwapSelector = "e7b7bb26";
        public const string VaultWithdrawSelector = "7864bf12";

        public const string KeyCurrentPrice = "current_price";
        public const string KeyCurrentTick = "current_tick";

        /// <summary>
        /// Finds the inner application call to the vault with the given ARC4 selector (searches nested inner transactions too).
        /// </summary>
        public static ApplicationNoopTransaction? FindVaultCall(SignedTransaction current, string selector)
        {
            return Find(current.Detail?.InnerTxns, selector);
        }

        private static ApplicationNoopTransaction? Find(IEnumerable<SignedTransaction>? txns, string selector)
        {
            if (txns == null) return null;
            foreach (var tx in txns)
            {
                if (tx.Tx is ApplicationNoopTransaction app && app.ApplicationId.HasValue && app.ApplicationId.Value != 0
                    && app.ApplicationArgs != null && app.ApplicationArgs.Count > 0
                    && Convert.ToHexString(app.ApplicationArgs.First()).Equals(selector, StringComparison.OrdinalIgnoreCase))
                {
                    return app;
                }
                var nested = Find(tx.Detail?.InnerTxns, selector);
                if (nested != null) return nested;
            }
            return null;
        }

        public static ulong GetUintArg(ApplicationNoopTransaction app, int index)
        {
            if (app.ApplicationArgs == null || app.ApplicationArgs.Count <= index) return 0;
            var bytes = app.ApplicationArgs.ElementAt(index);
            if (bytes == null || bytes.Length != 8) return 0;
            return BitConverter.ToUInt64(bytes.Reverse().ToArray(), 0);
        }

        /// <summary>
        /// Converts the contract sqrt price (Q64.64: sqrt(price in base units) * 2^64) to the decimal adjusted price of 1 A in B.
        /// </summary>
        public static decimal SqrtPriceX64ToPrice(ulong sqrtPriceX64, ulong assetADecimals, ulong assetBDecimals)
        {
            var sqrt = (double)sqrtPriceX64 / 18446744073709551616.0; // 2^64
            var priceBaseUnits = sqrt * sqrt;
            var price = priceBaseUnits * Math.Pow(10, (double)assetADecimals - (double)assetBDecimals);
            if (double.IsNaN(price) || double.IsInfinity(price)) return 0;
            return Convert.ToDecimal(price);
        }
    }
}
