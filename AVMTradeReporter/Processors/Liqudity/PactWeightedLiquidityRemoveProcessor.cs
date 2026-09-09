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
    /// Liquidity remove processor for Pact weighted pools. See <see cref="PactWeightedPoolHelper"/> for the contract layout.
    ///
    /// Group layout: [axfer LP token to pool account] [appl pool remove_liquidity(axfer,uint64,uint64)]
    /// Pool call inner txns: [appl vault] -> nested [pay|axfer A to provider] [pay|axfer B to provider]
    /// </summary>
    public class PactWeightedLiquidityRemoveProcessor : ILiquidityProcessor
    {
        public string AppArg { get; set; } = PactWeightedPoolHelper.RemoveLiquiditySelector;

        private readonly ILogger<TransactionProcessor> _logger;
        public PactWeightedLiquidityRemoveProcessor(ILogger<TransactionProcessor> logger)
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
            if (previous1 == null) return null;
            if (appCallTx.ApplicationId == null || appCallTx.ApplicationId == 0) return null;
            if (!PactWeightedPoolHelper.IsWeightedPoolCall(current)) return null;
            if (previous1.Tx is not AssetTransferTransaction lpTransfer) return null;

            var poolAddress = Address.ForApplication(appCallTx.ApplicationId.Value);
            var assetLPId = lpTransfer.XferAsset;
            var assetLPAmount = lpTransfer.AssetAmount;

            if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
            if (txGroup != null) current.Tx.Group = txGroup;

            var transfers = PactWeightedPoolHelper.GetAllInnerTransfers(current)
                .Where(t => PactWeightedPoolHelper.GetAssetId(t) != assetLPId)
                .ToList();
            if (transfers.Count < 2) return null;

            var outA = transfers[0];
            var outB = transfers[1];
            var assetAId = PactWeightedPoolHelper.GetAssetId(outA);
            var assetBId = PactWeightedPoolHelper.GetAssetId(outB);
            if (assetAId == assetBId) return null;

            var (A, B, L) = PactWeightedPoolHelper.GetReserves(current);

            return new Liquidity
            {
                Direction = LiquidityDirection.WithdrawLiquidity,
                AssetIdA = assetAId,
                AssetIdB = assetBId,
                AssetIdLP = assetLPId,
                AssetAmountA = PactWeightedPoolHelper.GetAmount(outA),
                AssetAmountB = PactWeightedPoolHelper.GetAmount(outB),
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
