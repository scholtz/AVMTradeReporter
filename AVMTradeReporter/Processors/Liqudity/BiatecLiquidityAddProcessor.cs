using Algorand;
using Algorand.Algod.Model.Transactions;
using AVM.ClientGenerator.Core;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace AVMTradeReporter.Processors.Liqudity
{
    public class BiatecLiquidityAddProcessor : ILiquidityProcessor
    {
        public string AppArg { get; set; } = "0440fa8f";

        public Liquidity? GetLiquidityUpdate(
            SignedTransaction current,
            SignedTransaction? previous1,
            SignedTransaction? previous2,
            Algorand.Algod.Model.Block? block,
            Digest? txGroup,
            string topTxId,
            Address liqudityProvider,
            TxState tradeState
            )
        {
            if (current.Tx is ApplicationNoopTransaction appCallTx)
            {
                if (previous1 == null)
                {
                    // This is the first transaction, no previous transaction to compare with.
                    return null;
                }
                if (previous2 == null)
                {
                    // This is the first transaction, no previous transaction to compare with.
                    return null;
                }
                ulong assetAId = 0;
                ulong assetAAmount = 0;
                Address? poolAddress = null;
                if (previous2.Tx is AssetTransferTransaction inAssetTransferTxA)
                {
                    assetAId = inAssetTransferTxA.XferAsset;
                    assetAAmount = inAssetTransferTxA.AssetAmount;
                    poolAddress = inAssetTransferTxA.AssetReceiver;
                }
                if (previous2.Tx is PaymentTransaction inPayTransferTxA)
                {
                    assetAAmount = inPayTransferTxA.Amount ?? 0;
                    poolAddress = inPayTransferTxA.Receiver;
                }
                if (poolAddress == null) return null;

                ulong assetBId = 0;
                ulong assetBAmount = 0;
                if (previous1.Tx is AssetTransferTransaction inAssetTransferTxB)
                {
                    assetBId = inAssetTransferTxB.XferAsset;
                    assetBAmount = inAssetTransferTxB.AssetAmount;
                    if (poolAddress != inAssetTransferTxB.AssetReceiver) return null;
                }
                if (previous1.Tx is PaymentTransaction inPayTransferTxB)
                {
                    assetBAmount = inPayTransferTxB.Amount ?? 0;
                    if (poolAddress != inPayTransferTxB.Receiver) return null;
                }

                ulong assetLPId = 0;
                ulong assetLPAmount = 0;
                var inner = current.Detail?.InnerTxns?.LastOrDefault()?.Tx;
                if (inner is AssetTransferTransaction outAssetTransferTx)
                {
                    assetLPId = outAssetTransferTx.XferAsset;
                    assetLPAmount = outAssetTransferTx.AssetAmount;
                }
                if (assetLPId == assetAId || assetLPId == assetBId)
                {
                    return null;
                }
                if (block != null) current.Tx.FillInParamsFromBlockHeader(block);

                ulong A = 0, B = 0, L = 0;
                var AItem = current.Detail?.GlobalDelta?.FirstOrDefault(kv => kv.Key.ToString() == "ab");
                if (AItem != null && AItem.Value.Value != null)
                {
                    if(AItem.Value.Value.Bytes is string stringVal)
                    {
                        var asciiBytes = Model.Utils.DeltaValueStringToBytes(stringVal);
                        A = Utils.UInt256Base64DeltaToUlong(asciiBytes);
                    }
                }
                var BItem = current.Detail?.GlobalDelta?.FirstOrDefault(kv => kv.Key.ToString() == "bb");
                if (BItem != null && BItem.Value.Value != null)
                {
                    if (BItem.Value.Value.Bytes is string stringVal)
                    {
                        var asciiBytes = Model.Utils.DeltaValueStringToBytes(stringVal);
                        B = Utils.UInt256Base64DeltaToUlong(asciiBytes);
                    }
                }
                var LItem = current.Detail?.GlobalDelta?.FirstOrDefault(kv => kv.Key.ToString() == "L");
                if (LItem != null && LItem.Value.Value != null)
                {
                    if (LItem.Value.Value.Bytes is string stringVal)
                    {
                        var asciiBytes = Model.Utils.DeltaValueStringToBytes(stringVal);
                        L = Utils.UInt256Base64DeltaToUlong(asciiBytes);
                    }
                }
                if (poolAddress == null) return null;
                return new Liquidity
                {
                    Direction = LiqudityDirection.DepositLiquidity,
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
                    Protocol = DEXProtocol.Biatec,
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
