using Algorand;
using Algorand.Algod.Model.Transactions;
using AVM.ClientGenerator.Core;
using AVMTradeReporter.Model;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;

namespace AVMTradeReporter.Processors.SWAP
{
    /// <summary>
    /// Swap processor for Pact weighted pools. See <see cref="PactWeightedPoolHelper"/> for the contract layout.
    ///
    /// Group layout: [pay|axfer deposit to vault address] [appl pool swap(txn,uint64)]
    /// Pool call inner txns: [appl vault] -> nested [pay|axfer output to trader]
    /// </summary>
    public class PactWeightedSwapProcessor : ISwapProcessor
    {
        public string AppArg { get; set; } = PactWeightedPoolHelper.SwapSelector;

        private readonly ILogger<TransactionProcessor> _logger;
        public PactWeightedSwapProcessor(ILogger<TransactionProcessor> logger)
        {
            _logger = logger;
        }

        public Trade? GetTrade(
            SignedTransaction current,
            SignedTransaction? previous,
            Algorand.Algod.Model.Block? block,
            Digest? txGroup,
            string topTxId,
            Address trader,
            TxState tradeState
            )
        {
            if (current.Tx is not ApplicationNoopTransaction appCallTx) return null;
            if (previous == null) return null;
            if (appCallTx.ApplicationId == null || appCallTx.ApplicationId == 0) return null;

            if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
            if (txGroup != null) current.Tx.Group = txGroup;

            ulong assetIdIn;
            ulong assetAmountIn;
            Address? vaultAddress;
            if (previous.Tx is AssetTransferTransaction inAxfer)
            {
                assetIdIn = inAxfer.XferAsset;
                assetAmountIn = inAxfer.AssetAmount;
                vaultAddress = inAxfer.AssetReceiver;
            }
            else if (previous.Tx is PaymentTransaction inPay)
            {
                assetIdIn = 0;
                assetAmountIn = inPay.Amount ?? 0;
                vaultAddress = inPay.Receiver;
            }
            else
            {
                return null;
            }
            if (vaultAddress == null) return null;

            ulong assetIdOut = 0;
            ulong assetAmountOut = 0;
            if (current.Detail?.InnerTxns == null || current.Detail.InnerTxns.Count == 0)
            {
                if (tradeState == TxState.Confirmed) return null;
            }
            else
            {
                if (!PactWeightedPoolHelper.IsWeightedPoolCall(current)) return null;
                var transfers = PactWeightedPoolHelper.GetAllInnerTransfers(current);
                // the vault may refund part of the input (same asset back to the sender) - subtract it
                foreach (var refund in transfers.Where(t => PactWeightedPoolHelper.GetAssetId(t) == assetIdIn && t.Sender == vaultAddress))
                {
                    var refundAmount = PactWeightedPoolHelper.GetAmount(refund);
                    assetAmountIn = assetAmountIn > refundAmount ? assetAmountIn - refundAmount : 0;
                }
                var output = transfers.LastOrDefault(t => PactWeightedPoolHelper.GetAssetId(t) != assetIdIn);
                if (output == null) return null;
                assetIdOut = PactWeightedPoolHelper.GetAssetId(output);
                assetAmountOut = PactWeightedPoolHelper.GetAmount(output);
            }

            var (A, B, L) = PactWeightedPoolHelper.GetReserves(current);
            // funds live in the shared vault; the pool identity is the pool application account
            var poolAddress = Address.ForApplication(appCallTx.ApplicationId.Value);

            return new Trade
            {
                AssetIdIn = assetIdIn,
                AssetIdOut = assetIdOut,
                AssetAmountIn = assetAmountIn,
                AssetAmountOut = assetAmountOut,
                TxId = current.Tx.TxID(),
                BlockId = block?.Round ?? 0,
                TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                Timestamp = block == null ? DateTimeOffset.UtcNow : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block?.Timestamp ?? 0)),
                Protocol = DEXProtocol.Pact,
                PoolAddress = poolAddress.EncodeAsString(),
                PoolAppId = appCallTx.ApplicationId.Value,
                TopTxId = topTxId,
                Trader = trader.EncodeAsString(),
                TradeState = tradeState,
                A = A,
                B = B,
                L = L
            };
        }
    }
}
