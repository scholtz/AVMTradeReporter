using Algorand;
using Algorand.Algod.Model.Transactions;
using AVM.ClientGenerator.Core;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.Data.Enums;
using System.Threading;

namespace AVMTradeReporter.Processors.SWAP
{
    public class PactSwapProcessor : ISwapProcessor
    {
        public string AppArg { get; set; } = "53574150";

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
            if (current.Tx is ApplicationNoopTransaction appCallTx)
            {
                if (previous == null)
                {
                    // This is the first transaction, no previous transaction to compare with.
                    return null;
                }
                ulong AssetIdIn = 0;
                ulong AssetIdOut = 0;
                ulong AssetAmountIn = 0;
                ulong AssetAmountOut = 0;
                Address? poolAddress = null;


                if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
                if (txGroup != null) current.Tx.Group = txGroup;

                if (previous.Tx is AssetTransferTransaction inAssetTransferTx)
                {
                    AssetIdIn = inAssetTransferTx.XferAsset;
                    AssetAmountIn = inAssetTransferTx.AssetAmount;
                    poolAddress = inAssetTransferTx.AssetReceiver;
                    // from asa
                    if (current.Detail?.InnerTxns == null)
                    {
                        if (tradeState == TxState.Confirmed) return null;

                        AssetIdOut = 0;
                        AssetAmountOut = 0;
                    }
                    else
                    {

                        var inner1 = current.Detail.InnerTxns.FirstOrDefault()?.Tx;
                        if (current.Detail.InnerTxns.Count == 2 && inner1 is AssetTransferTransaction outPayTransferTx && inAssetTransferTx.XferAsset == outPayTransferTx.XferAsset)
                        {
                            // first tx is return tx to the deposit
                            // to asa
                            AssetAmountIn = inAssetTransferTx.AssetAmount - outPayTransferTx.AssetAmount;
                        }

                        var inner2 = current.Detail.InnerTxns.LastOrDefault()?.Tx;
                        if (inner2 is AssetTransferTransaction outAssetTransferTx)
                        {
                            // to asa
                            AssetIdOut = outAssetTransferTx.XferAsset;
                            AssetAmountOut = outAssetTransferTx.AssetAmount;
                        }

                        if (inner2 is PaymentTransaction outPaymentTx)
                        {
                            // to native

                            AssetIdOut = 0;
                            AssetAmountOut = outPaymentTx.Amount ?? 0;
                        }
                    }
                }
                if (previous.Tx is PaymentTransaction inPayTx)
                {
                    AssetIdIn = 0;
                    AssetAmountIn = inPayTx.Amount ?? 0;
                    poolAddress = inPayTx.Receiver;
                    // from native
                    if (current.Detail?.InnerTxns == null)
                    {
                        if (tradeState == TxState.Confirmed) return null;
                        AssetIdOut = 0;
                        AssetAmountOut = 0;
                    }
                    else
                    {
                        var inner1 = current.Detail.InnerTxns.FirstOrDefault()?.Tx;
                        if (current.Detail.InnerTxns.Count == 2 && inner1 is PaymentTransaction outPayTransferTx)
                        {
                            // first tx is return tx to the deposit
                            // to asa
                            AssetAmountIn = inPayTx.Amount ?? 0 - outPayTransferTx.Amount ?? 0;
                        }

                        var inner2 = current.Detail.InnerTxns.LastOrDefault()?.Tx;
                        if (inner2 is AssetTransferTransaction outAssetTransferTx)
                        {
                            // to asa
                            AssetIdOut = outAssetTransferTx.XferAsset;
                            AssetAmountOut = outAssetTransferTx.AssetAmount;
                        }

                    }
                }
                if (poolAddress == null) return null;

                ulong A = 0, B = 0, L = 0;
                var AItem = current.Detail?.GlobalDelta?.FirstOrDefault(kv => kv.Key.ToString() == "A");
                if (AItem != null && AItem.Value.Value != null)
                {
                    A = Convert.ToUInt64(AItem.Value.Value.Uint64);
                }
                var BItem = current.Detail?.GlobalDelta?.FirstOrDefault(kv => kv.Key.ToString() == "B");
                if (BItem != null && BItem.Value.Value != null)
                {
                    B = Convert.ToUInt64(BItem.Value.Value.Uint64);
                }
                var LItem = current.Detail?.GlobalDelta?.FirstOrDefault(kv => kv.Key.ToString() == "L");
                if (LItem != null && LItem.Value.Value != null)
                {
                    L = Convert.ToUInt64(LItem.Value.Value.Uint64);
                }

                var trade = new Trade
                {
                    AssetIdIn = AssetIdIn,
                    AssetIdOut = AssetIdOut,
                    AssetAmountIn = AssetAmountIn,
                    AssetAmountOut = AssetAmountOut,
                    TxId = current.Tx.TxID(),
                    BlockId = block?.Round ?? 0,
                    TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                    Timestamp = block == null ? DateTimeOffset.UtcNow : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block?.Timestamp ?? 0)),
                    Protocol = DEXProtocol.Pact,
                    PoolAddress = poolAddress.EncodeAsString(),
                    PoolAppId = appCallTx.ApplicationId ?? 0,
                    TopTxId = topTxId,
                    Trader = trader.EncodeAsString(),
                    TradeState = tradeState,
                    A = A,
                    B = B,
                    L = L
                };
                return trade;
            }
            return null;
        }
    }
}
