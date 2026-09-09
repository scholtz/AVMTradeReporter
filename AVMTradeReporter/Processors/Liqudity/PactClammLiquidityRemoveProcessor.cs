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
    /// Liquidity remove (burn) processor for Pact CLAMM (tick based concentrated liquidity) pools. See <see cref="PactClammHelper"/>.
    ///
    /// Group layout: [appl pool burn(tick_low, tick_high)] - no LP token transfer precedes the call
    /// Pool call inner txns: [budget appl 0 ...] [appl vault withdraw(asset_a, amt_a, asset_b, amt_b, .., recipient)] -> nested [pay|axfer A] [pay|axfer B] to the provider
    /// </summary>
    public class PactClammLiquidityRemoveProcessor : ILiquidityProcessor
    {
        public string AppArg { get; set; } = PactClammHelper.BurnSelector;

        private readonly ILogger<TransactionProcessor> _logger;
        public PactClammLiquidityRemoveProcessor(ILogger<TransactionProcessor> logger)
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
            if (appCallTx.ApplicationId == null || appCallTx.ApplicationId == 0) return null;

            var vaultWithdraw = PactClammHelper.FindVaultCall(current, PactClammHelper.VaultWithdrawSelector);
            if (vaultWithdraw == null) return null;

            var assetAId = PactClammHelper.GetUintArg(vaultWithdraw, 1);
            var assetAAmount = PactClammHelper.GetUintArg(vaultWithdraw, 2);
            var assetBId = PactClammHelper.GetUintArg(vaultWithdraw, 3);
            var assetBAmount = PactClammHelper.GetUintArg(vaultWithdraw, 4);
            if (assetAId == assetBId) return null;

            // prefer the real transfers when present
            var transfers = PactWeightedPoolHelper.GetAllInnerTransfers(current);
            var outA = transfers.FirstOrDefault(t => PactWeightedPoolHelper.GetAssetId(t) == assetAId);
            var outB = transfers.FirstOrDefault(t => PactWeightedPoolHelper.GetAssetId(t) == assetBId);
            if (outA != null) assetAAmount = PactWeightedPoolHelper.GetAmount(outA);
            if (outB != null) assetBAmount = PactWeightedPoolHelper.GetAmount(outB);

            if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
            if (txGroup != null) current.Tx.Group = txGroup;

            var poolAddress = Address.ForApplication(appCallTx.ApplicationId.Value);

            return new Liquidity
            {
                Direction = LiquidityDirection.WithdrawLiquidity,
                AssetIdA = assetAId,
                AssetIdB = assetBId,
                AssetIdLP = 0,
                AssetAmountA = assetAAmount,
                AssetAmountB = assetBAmount,
                AssetAmountLP = 0,
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
                A = 0,
                B = 0,
                L = 0
            };
        }
    }
}
