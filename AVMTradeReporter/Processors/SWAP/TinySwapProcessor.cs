using Algorand;
using Algorand.Algod.Model.Transactions;
using AVM.ClientGenerator.Core;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Data;
using System.Threading;

namespace AVMTradeReporter.Processors.SWAP
{
    public class TinySwapProcessor : ISwapProcessor
    {
        public string AppArg { get; set; } = "73776170";

        public Trade? GetTrade(
            SignedTransaction current,
            SignedTransaction? previous,
            Algorand.Algod.Model.Block? block,
            Digest? txGroup,
            string topTxId,
            Address trader,
            TradeState tradeState
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

                ulong A = 0, B = 0, L = 0;

                var AItem = current.Detail?.LocalDelta?.SelectMany(k => k.Value)?.FirstOrDefault(kv => kv.Key.ToString() == "asset_1_reserves");
                if (AItem != null)
                {
                    A = Convert.ToUInt64(AItem.Value.Value.Uint64);
                }
                else
                {
                    return null;// tiny swap has always the local delta change
                }
                var BItem = current.Detail?.LocalDelta?.SelectMany(k => k.Value)?.FirstOrDefault(kv => kv.Key.ToString() == "asset_2_reserves");
                if (BItem != null)
                {
                    B = Convert.ToUInt64(BItem.Value.Value.Uint64);
                }
                else
                {
                    return null; // tiny swap has always the local delta change
                }

                if (previous.Tx is AssetTransferTransaction inAssetTransferTx)
                {
                    // from asa
                    poolAddress = inAssetTransferTx.AssetReceiver;
                    if (current.Detail?.InnerTxns == null)
                    {
                        if (tradeState == TradeState.Confirmed) return null;

                        AssetIdIn = inAssetTransferTx.XferAsset;
                        AssetIdOut = 0;
                        AssetAmountIn = inAssetTransferTx.AssetAmount;
                        AssetAmountOut = 0;
                    }
                    else
                    {
                        var inner = current.Detail.InnerTxns.FirstOrDefault()?.Tx;
                        if (inner is AssetTransferTransaction outAssetTransferTx)
                        {
                            // to asa

                            AssetIdIn = inAssetTransferTx.XferAsset;
                            AssetIdOut = outAssetTransferTx.XferAsset;
                            AssetAmountIn = inAssetTransferTx.AssetAmount;
                            AssetAmountOut = outAssetTransferTx.AssetAmount;
                        }
                        if (inner is PaymentTransaction outPaymentTx)
                        {
                            // to native

                            AssetIdIn = inAssetTransferTx.XferAsset;
                            AssetIdOut = 0;
                            AssetAmountIn = inAssetTransferTx.AssetAmount;
                            AssetAmountOut = outPaymentTx.Amount ?? 0;
                        }
                    }
                }
                if (previous.Tx is PaymentTransaction inPayTx)
                {
                    poolAddress = inPayTx.Receiver;
                    // from native
                    if (current.Detail?.InnerTxns == null)
                    {
                        if (tradeState == TradeState.Confirmed) return null;

                        AssetIdIn = 0;
                        AssetIdOut = 0;
                        AssetAmountIn = inPayTx.Amount ?? 0;
                        AssetAmountOut = 0;
                    }
                    else
                    {
                        var inner = current.Detail.InnerTxns.FirstOrDefault()?.Tx;
                        if (inner is AssetTransferTransaction outAssetTransferTx)
                        {
                            // to asa

                            AssetIdIn = 0;
                            AssetIdOut = outAssetTransferTx.XferAsset;
                            AssetAmountIn = inPayTx.Amount ?? 0;
                            AssetAmountOut = outAssetTransferTx.AssetAmount;
                        }
                    }
                }
                if (poolAddress == null) return null;

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
