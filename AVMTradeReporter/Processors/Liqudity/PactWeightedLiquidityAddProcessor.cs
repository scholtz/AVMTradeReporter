using Algorand;
using Algorand.Algod.Model.Transactions;
using AVM.ClientGenerator.Core;
using AVMTradeReporter.Model;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;

namespace AVMTradeReporter.Processors.Liqudity
{
    /// <summary>
    /// Liquidity add processor for Pact weighted pools. See <see cref="PactWeightedPoolHelper"/> for the contract layout.
    ///
    /// Group layout: [pay|axfer A deposit to vault] [pay|axfer B deposit to vault] [appl pool add_liquidity(txn,txn)]
    /// Pool call inner txns: [appl vault] [axfer LP token from pool account to provider]
    /// </summary>
    public class PactWeightedLiquidityAddProcessor : ILiquidityProcessor
    {
        public string AppArg { get; set; } = PactWeightedPoolHelper.AddLiquiditySelector;

        private readonly ILogger<TransactionProcessor> _logger;
        public PactWeightedLiquidityAddProcessor(ILogger<TransactionProcessor> logger)
        {
            _logger = logger;
        }

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
            if (current.Tx is not ApplicationNoopTransaction appCallTx) return null;
            if (previous1 == null || previous2 == null) return null;
            if (appCallTx.ApplicationId == null || appCallTx.ApplicationId == 0) return null;
            if (!PactWeightedPoolHelper.IsWeightedPoolCall(current)) return null;

            if (previous2.Tx is not (AssetTransferTransaction or PaymentTransaction)) return null;
            if (previous1.Tx is not (AssetTransferTransaction or PaymentTransaction)) return null;

            var vaultAddress = PactWeightedPoolHelper.GetReceiver(previous2.Tx);
            if (vaultAddress == null) return null;
            if (PactWeightedPoolHelper.GetReceiver(previous1.Tx) != vaultAddress) return null;

            var assetAId = PactWeightedPoolHelper.GetAssetId(previous2.Tx);
            var assetAAmount = PactWeightedPoolHelper.GetAmount(previous2.Tx);
            var assetBId = PactWeightedPoolHelper.GetAssetId(previous1.Tx);
            var assetBAmount = PactWeightedPoolHelper.GetAmount(previous1.Tx);

            if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
            if (txGroup != null) current.Tx.Group = txGroup;

            var poolAddress = Address.ForApplication(appCallTx.ApplicationId.Value);

            ulong assetLPId = 0;
            ulong assetLPAmount = 0;
            var transfers = PactWeightedPoolHelper.GetAllInnerTransfers(current);
            foreach (var transfer in transfers)
            {
                var assetId = PactWeightedPoolHelper.GetAssetId(transfer);
                var amount = PactWeightedPoolHelper.GetAmount(transfer);
                if (transfer is AssetTransferTransaction && transfer.Sender == poolAddress && assetId != assetAId && assetId != assetBId)
                {
                    // LP token minted by the pool account
                    assetLPId = assetId;
                    assetLPAmount = amount;
                }
                else if (transfer.Sender == vaultAddress)
                {
                    // refund of the excess deposit
                    if (assetId == assetAId) assetAAmount = assetAAmount > amount ? assetAAmount - amount : 0;
                    else if (assetId == assetBId) assetBAmount = assetBAmount > amount ? assetBAmount - amount : 0;
                }
            }
            if (assetLPId == 0) return null;

            var (A, B, L) = PactWeightedPoolHelper.GetReserves(current);

            return new Liquidity
            {
                Direction = LiquidityDirection.DepositLiquidity,
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
                PoolAppId = appCallTx.ApplicationId.Value,
                TopTxId = topTxId,
                LiquidityProvider = liqudityProvider.EncodeAsString(),
                TxState = tradeState,
                A = A,
                B = B,
                L = L
            };
        }
    }
}
