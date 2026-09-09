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
    /// Swap processor for Pact CLAMM (tick based concentrated liquidity) pools. See <see cref="PactClammHelper"/>.
    ///
    /// Group layout: [pay|axfer deposit to vault address] [appl pool swap(txn,uint64)]
    /// Pool call inner txns: [budget appl 0 ...] [appl vault swap(asset_in, amt_in, asset_out, amt_out, fee, .., recipient)] -> nested [pay|axfer output to trader]
    /// The pool does not expose reserves, so A / B / L are reported as 0 and the pool reserves are tracked incrementally.
    /// </summary>
    public class PactClammSwapProcessor : ISwapProcessor
    {
        public string AppArg { get; set; } = PactClammHelper.SwapSelector;

        private readonly ILogger<TransactionProcessor> _logger;
        public PactClammSwapProcessor(ILogger<TransactionProcessor> logger)
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
                var vaultSwap = PactClammHelper.FindVaultCall(current, PactClammHelper.VaultSwapSelector);
                if (vaultSwap == null) return null;
                if (PactClammHelper.GetUintArg(vaultSwap, 1) != assetIdIn) return null;
                assetIdOut = PactClammHelper.GetUintArg(vaultSwap, 3);
                assetAmountOut = PactClammHelper.GetUintArg(vaultSwap, 4);

                // prefer the real transfer if present
                var transfers = PactWeightedPoolHelper.GetAllInnerTransfers(current);
                var output = transfers.LastOrDefault(t => PactWeightedPoolHelper.GetAssetId(t) == assetIdOut && t.Sender == vaultAddress);
                if (output != null)
                {
                    assetAmountOut = PactWeightedPoolHelper.GetAmount(output);
                }
                foreach (var refund in transfers.Where(t => PactWeightedPoolHelper.GetAssetId(t) == assetIdIn && t.Sender == vaultAddress))
                {
                    var refundAmount = PactWeightedPoolHelper.GetAmount(refund);
                    assetAmountIn = assetAmountIn > refundAmount ? assetAmountIn - refundAmount : 0;
                }
            }

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
                A = 0,
                B = 0,
                L = 0
            };
        }
    }
}
