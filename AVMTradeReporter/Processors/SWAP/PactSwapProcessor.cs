using Algorand;
using Algorand.Algod.Model.Transactions;
using AVM.ClientGenerator.Core;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Data;
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
                if (previous.Tx is AssetTransferTransaction inAssetTransferTx)
                {
                    // from asa
                    if (current.Detail?.InnerTxns == null)
                    {
                        if (tradeState == TradeState.Confirmed) return null;
                        var trade = new Trade
                        {
                            AssetIdIn = inAssetTransferTx.XferAsset,
                            AssetIdOut = 0,
                            AssetAmountIn = inAssetTransferTx.AssetAmount,
                            AssetAmountOut = 0,
                            TxId = current.Tx.TxID(),
                            BlockId = block?.Round ?? 0,
                            TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                            Timestamp = block == null ? DateTimeOffset.UtcNow : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block?.Timestamp ?? 0)),
                            Protocol = DEXProtocol.Pact,
                            PoolAddress = inAssetTransferTx.AssetReceiver.EncodeAsString(),
                            PoolAppId = appCallTx.ApplicationId ?? 0,
                            TopTxId = topTxId,
                            Trader = trader.EncodeAsString(),
                            TradeState = tradeState
                        };
                        return trade;
                    }
                    else
                    {
                        var inner = current.Detail.InnerTxns.FirstOrDefault()?.Tx;
                        if (inner is AssetTransferTransaction outAssetTransferTx)
                        {
                            // to asa

                            if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
                            if (txGroup != null) current.Tx.Group = txGroup;
                            var trade = new Trade
                            {
                                AssetIdIn = inAssetTransferTx.XferAsset,
                                AssetIdOut = outAssetTransferTx.XferAsset,
                                AssetAmountIn = inAssetTransferTx.AssetAmount,
                                AssetAmountOut = outAssetTransferTx.AssetAmount,
                                TxId = current.Tx.TxID(),
                                BlockId = block?.Round ?? 0,
                                TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                Protocol = DEXProtocol.Pact,
                                PoolAddress = outAssetTransferTx.Sender.EncodeAsString(),
                                PoolAppId = appCallTx.ApplicationId ?? 0,
                                TopTxId = topTxId,
                                Trader = trader.EncodeAsString(),
                                TradeState = tradeState
                            };
                            return trade;
                        }
                        if (inner is PaymentTransaction outPaymentTx)
                        {
                            // to native
                            if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
                            if (txGroup != null) current.Tx.Group = txGroup;

                            var trade = new Trade
                            {
                                AssetIdIn = inAssetTransferTx.XferAsset,
                                AssetIdOut = 0,
                                AssetAmountIn = inAssetTransferTx.AssetAmount,
                                AssetAmountOut = outPaymentTx.Amount ?? 0,
                                TxId = current.Tx.TxID(),
                                BlockId = block?.Round ?? 0,
                                TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                Protocol = DEXProtocol.Pact,
                                PoolAddress = outPaymentTx.Sender.EncodeAsString(),
                                PoolAppId = appCallTx.ApplicationId ?? 0,
                                TopTxId = topTxId,
                                Trader = trader.EncodeAsString(),
                                TradeState = tradeState
                            };
                            return trade;
                        }
                    }
                }
                if (previous.Tx is PaymentTransaction inPayTx)
                {
                    // from native
                    if (current.Detail?.InnerTxns == null)
                    {
                        if (tradeState == TradeState.Confirmed) return null;
                        var trade = new Trade
                        {
                            AssetIdIn = 0,
                            AssetIdOut = 0,
                            AssetAmountIn = inPayTx.Amount ?? 0,
                            AssetAmountOut = 0,
                            TxId = current.Tx.TxID(),
                            BlockId = block?.Round ?? 0,
                            TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                            Timestamp = block == null ? DateTimeOffset.UtcNow : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block?.Timestamp ?? 0)),
                            Protocol = DEXProtocol.Pact,
                            PoolAddress = inPayTx.Receiver.EncodeAsString(),
                            PoolAppId = appCallTx.ApplicationId ?? 0,
                            TopTxId = topTxId,
                            Trader = trader.EncodeAsString(),
                            TradeState = tradeState
                        };
                        return trade;
                    }
                    else
                    {
                        var inner = current.Detail.InnerTxns.FirstOrDefault()?.Tx;
                        if (inner is AssetTransferTransaction outAssetTransferTx)
                        {
                            // to asa
                            if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
                            if (txGroup != null) current.Tx.Group = txGroup;

                            var trade = new Trade
                            {
                                AssetIdIn = 0,
                                AssetIdOut = outAssetTransferTx.XferAsset,
                                AssetAmountIn = inPayTx.Amount ?? 0,
                                AssetAmountOut = outAssetTransferTx.AssetAmount,
                                TxId = current.Tx.TxID(),
                                BlockId = block?.Round ?? 0,
                                TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                Protocol = DEXProtocol.Pact,
                                PoolAddress = outAssetTransferTx.Sender.EncodeAsString(),
                                PoolAppId = appCallTx.ApplicationId ?? 0,
                                TopTxId = topTxId,
                                Trader = trader.EncodeAsString(),
                                TradeState = tradeState
                            };
                            return trade;
                        }
                    }
                }
            }
            return null;
        }
    }
}
