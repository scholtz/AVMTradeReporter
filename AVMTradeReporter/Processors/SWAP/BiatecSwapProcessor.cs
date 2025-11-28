using Algorand;
using Algorand.Algod.Model.Transactions;
using AVM.ClientGenerator.Core;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using System.Threading;

namespace AVMTradeReporter.Processors.SWAP
{
    public class BiatecSwapProcessor : ISwapProcessor
    {
        public string AppArg { get; set; } = "2013349e";

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
                    poolAddress = inAssetTransferTx.AssetReceiver;

                    if (current.Detail?.InnerTxns == null)
                    {
                        if (tradeState == TxState.Confirmed) return null;
                        AssetIdIn = inAssetTransferTx.XferAsset;
                        AssetIdOut = 0;
                        AssetAmountIn = inAssetTransferTx.AssetAmount;
                        AssetAmountOut = 0;
                    }
                    else
                    {
                        // from asa
                        foreach (var inner in current.Detail.InnerTxns)
                        {
                            // find first axfer or pay tx

                            if (inner.Tx is AssetTransferTransaction outAssetTransferTx)
                            {
                                // to asa


                                AssetIdIn = inAssetTransferTx.XferAsset;
                                AssetIdOut = outAssetTransferTx.XferAsset;
                                AssetAmountIn = inAssetTransferTx.AssetAmount;
                                AssetAmountOut = outAssetTransferTx.AssetAmount;

                            }
                            if (inner.Tx is PaymentTransaction outPaymentTx)
                            {
                                // to native

                                AssetIdIn = inAssetTransferTx.XferAsset;
                                AssetIdOut = 0;
                                AssetAmountIn = inAssetTransferTx.AssetAmount;
                                AssetAmountOut = outPaymentTx.Amount ?? 0;
                            }
                        }
                    }
                }
                if (previous.Tx is PaymentTransaction inPayTx)
                {
                    poolAddress = inPayTx.Receiver;
                    // from native
                    if (current.Detail?.InnerTxns == null)
                    {
                        if (tradeState == TxState.Confirmed) return null;

                        AssetIdIn = 0;
                        AssetIdOut = 0;
                        AssetAmountIn = inPayTx.Amount ?? 0;
                        AssetAmountOut = 0;

                    }
                    else
                    {
                        foreach (var inner in current.Detail.InnerTxns)
                        {
                            // find first axfer or pay tx
                            if (inner.Tx is AssetTransferTransaction outAssetTransferTx)
                            {
                                // to asa

                                AssetIdIn = 0;
                                AssetIdOut = outAssetTransferTx.XferAsset;
                                AssetAmountIn = inPayTx.Amount ?? 0;
                                AssetAmountOut = outAssetTransferTx.AssetAmount;
                                break;
                            }
                        }
                    }

                }
                if (poolAddress == null) return null;

                ulong A = 0, B = 0, L = 0;
                var AItem = current.Detail?.GlobalDelta?.FirstOrDefault(kv => kv.Key.ToString() == "ab");
                if (AItem != null && AItem.Value.Value != null)
                {
                    if (AItem.Value.Value.Bytes is string stringVal)
                    {
                        A = Utils.UInt256Base64DeltaToUlong(stringVal);
                    }
                }
                var BItem = current.Detail?.GlobalDelta?.FirstOrDefault(kv => kv.Key.ToString() == "bb");
                if (BItem != null && BItem.Value.Value != null)
                {
                    if (BItem.Value.Value.Bytes is string stringVal)
                    {
                        B = Utils.UInt256Base64DeltaToUlong(stringVal);
                    }
                }
                var LItem = current.Detail?.GlobalDelta?.FirstOrDefault(kv => kv.Key.ToString() == "L");
                if (LItem != null && LItem.Value.Value != null)
                {
                    if (LItem.Value.Value.Bytes is string stringVal)
                    {
                        L = Utils.UInt256Base64DeltaToUlong(stringVal);
                    }
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
                    Protocol = DEXProtocol.Biatec,
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
