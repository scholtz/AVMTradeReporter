using Algorand;
using Algorand.Algod.Model.Transactions;
using AVM.ClientGenerator.Core;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Data;
using System.Diagnostics;
using System.Threading;

namespace AVMTradeReporter.Processors.Liqudity
{
    public class PactLiquidityRemoveProcessor : ILiquidityProcessor
    {
        public string AppArg { get; set; } = "52454D4C4951";

        public Liquidity? GetLiquidityUpdate(
            SignedTransaction current,
            SignedTransaction? previous1,
            SignedTransaction? previous2,
            Algorand.Algod.Model.Block? block,
            Digest? txGroup,
            string topTxId,
            Address liqudityProvider,
            TradeState tradeState
            )
        {
            if (current.Tx is ApplicationNoopTransaction appCallTx)
            {
                if (previous1 == null)
                {
                    // This is the first transaction, no previous transaction to compare with.
                    return null;
                }
                ulong assetAId = 0;
                ulong assetAAmount = 0;
                Address? poolAddress = null;

                ulong assetBId = 0;
                ulong assetBAmount = 0;

                ulong assetLPId = 0;
                ulong assetLPAmount = 0;

                if (previous1.Tx is AssetTransferTransaction inAssetTransferTxB)
                {
                    assetLPId = inAssetTransferTxB.XferAsset;
                    assetLPAmount = inAssetTransferTxB.AssetAmount;
                    poolAddress = inAssetTransferTxB.AssetReceiver;
                }
                if (previous1.Tx is PaymentTransaction inPayTransferTxB)
                {
                    assetLPAmount = inPayTransferTxB.Amount ?? 0;
                    poolAddress = inPayTransferTxB.Receiver;
                }

                var inner = current.Detail?.InnerTxns?.FirstOrDefault()?.Tx;
                if (inner is AssetTransferTransaction outAssetTransferTx)
                {
                    assetAId = outAssetTransferTx.XferAsset;
                    assetAAmount = outAssetTransferTx.AssetAmount;
                }
                if (inner is PaymentTransaction outPayTransferTx)
                {
                    assetAAmount = outPayTransferTx.Amount ?? 0;
                }

                if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
                var inner2 = current.Detail.InnerTxns.Skip(1).FirstOrDefault()?.Tx;
                if (inner2 is AssetTransferTransaction outAssetTransferTx2)
                {
                    assetBId = outAssetTransferTx2.XferAsset;
                    assetBAmount = outAssetTransferTx2.AssetAmount;
                }
                if (inner2 is PaymentTransaction outPayTransferTx2)
                {
                    assetAAmount = outPayTransferTx2.Amount ?? 0;
                }
                if (assetLPId == assetAId || assetLPId == assetBId)
                {
                    return null;
                }

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
                if (poolAddress == null) return null;
                return new Liquidity
                {
                    Direction = LiqudityDirection.WithdrawLiquidity,
                    AssetIdA = assetAId,
                    AssetIdB = assetBId,
                    AssetIdLP = assetLPId,
                    AssetAmountA = assetAAmount,
                    AssetAmountB = assetBAmount,
                    AssetAmountLP = assetLPAmount,
                    TxId = current.Tx.TxID(),
                    BlockId = block?.Round ?? 0,
                    TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block?.Timestamp ?? 0)),
                    Protocol = DEXProtocol.Pact,
                    PoolAddress = poolAddress.EncodeAsString(),
                    PoolAppId = appCallTx.ApplicationId ?? 0,
                    TopTxId = topTxId,
                    LiquidityProvider = liqudityProvider.EncodeAsString(),
                    TxState = tradeState,
                    A = A,
                    B = B,
                    L = L
                };
            }
            return null;
        }
    }
}
