using Algorand;
using Algorand.Algod.Model.Transactions;

namespace AVMTradeReporter.Processors
{
    /// <summary>
    /// Shared helpers for the Pact "weighted pool" contracts (ARC4 contracts, e.g. app 3689973907).
    ///
    /// Differences from the classic Pact AMM:
    /// - app args are ARC4 method selectors (swap(txn,uint64)uint64, add_liquidity(txn,txn)uint64, remove_liquidity(axfer,uint64,uint64)void)
    /// - the funds are not held by the pool application account but by a shared "vault" application account,
    ///   so the deposit transaction receiver is the vault address and outgoing transfers are nested inner transactions
    ///   of the pool -> vault inner app call
    /// - global state keys are reserve_a / reserve_b / issued_lp instead of A / B / L
    /// </summary>
    public static class PactWeightedPoolHelper
    {
        public const string SwapSelector = "035942b0"; // swap(txn,uint64)uint64
        public const string AddLiquiditySelector = "644921bf"; // add_liquidity(txn,txn)uint64
        public const string RemoveLiquiditySelector = "53a36b24"; // remove_liquidity(axfer,uint64,uint64)void

        public const string KeyReserveA = "reserve_a";
        public const string KeyReserveB = "reserve_b";
        public const string KeyIssuedLP = "issued_lp";

        /// <summary>
        /// Returns true when the app call touched the weighted pool state (reserve_a / reserve_b keys).
        /// Used to make sure a selector collision with some other ARC4 contract does not produce bogus records.
        /// </summary>
        public static bool IsWeightedPoolCall(SignedTransaction current)
        {
            var delta = current.Detail?.GlobalDelta;
            if (delta == null) return false;
            return delta.Any(kv => kv.Key.ToString() == KeyReserveA) || delta.Any(kv => kv.Key.ToString() == KeyReserveB);
        }

        public static (ulong A, ulong B, ulong L) GetReserves(SignedTransaction current)
        {
            ulong A = 0, B = 0, L = 0;
            var delta = current.Detail?.GlobalDelta;
            if (delta == null) return (A, B, L);
            var AItem = delta.Where(kv => kv.Key.ToString() == KeyReserveA).Select(kv => kv.Value).FirstOrDefault();
            if (AItem != null) A = Convert.ToUInt64(AItem.Uint64);
            var BItem = delta.Where(kv => kv.Key.ToString() == KeyReserveB).Select(kv => kv.Value).FirstOrDefault();
            if (BItem != null) B = Convert.ToUInt64(BItem.Uint64);
            var LItem = delta.Where(kv => kv.Key.ToString() == KeyIssuedLP).Select(kv => kv.Value).FirstOrDefault();
            if (LItem != null) L = Convert.ToUInt64(LItem.Uint64);
            return (A, B, L);
        }

        /// <summary>
        /// Flattens all pay / axfer transactions from the inner transactions of the pool call, including
        /// the nested inner transactions sent by the vault application.
        /// </summary>
        public static List<Transaction> GetAllInnerTransfers(SignedTransaction current)
        {
            var result = new List<Transaction>();
            Collect(current.Detail?.InnerTxns, result);
            return result;
        }

        private static void Collect(IEnumerable<SignedTransaction>? txns, List<Transaction> result)
        {
            if (txns == null) return;
            foreach (var tx in txns)
            {
                if (tx.Tx is PaymentTransaction || tx.Tx is AssetTransferTransaction)
                {
                    result.Add(tx.Tx);
                }
                Collect(tx.Detail?.InnerTxns, result);
            }
        }

        public static ulong GetAssetId(Transaction tx)
        {
            return tx is AssetTransferTransaction axfer ? axfer.XferAsset : 0;
        }

        public static ulong GetAmount(Transaction tx)
        {
            if (tx is AssetTransferTransaction axfer) return axfer.AssetAmount;
            if (tx is PaymentTransaction pay) return pay.Amount ?? 0;
            return 0;
        }

        public static Address? GetReceiver(Transaction tx)
        {
            if (tx is AssetTransferTransaction axfer) return axfer.AssetReceiver;
            if (tx is PaymentTransaction pay) return pay.Receiver;
            return null;
        }
    }
}
