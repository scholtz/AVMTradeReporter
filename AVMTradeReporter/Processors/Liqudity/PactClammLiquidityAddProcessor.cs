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
    /// Liquidity add (mint) processor for Pact CLAMM (tick based concentrated liquidity) pools. See <see cref="PactClammHelper"/>.
    ///
    /// Group layout: [pay|axfer A deposit to vault] [pay|axfer B deposit to vault] [appl pool mint(tick_low, tick_high, txn, txn)]
    /// Pool call inner txns: [budget appl 0 ...] [appl vault deposit(asset_a, amt_a, asset_b, amt_b, ..)]
    /// There is no LP token (positions are boxes), so AssetIdLP / AssetAmountLP are 0. The amounts credited by the vault
    /// are used (they can differ slightly from the deposited amounts).
    /// </summary>
    public class PactClammLiquidityAddProcessor : ILiquidityProcessor
    {
        public string AppArg { get; set; } = PactClammHelper.MintSelector;

        private readonly ILogger<TransactionProcessor> _logger;
        public PactClammLiquidityAddProcessor(ILogger<TransactionProcessor> logger)
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
            if (previous2.Tx is not (AssetTransferTransaction or PaymentTransaction)) return null;
            if (previous1.Tx is not (AssetTransferTransaction or PaymentTransaction)) return null;

            var vaultDeposit = PactClammHelper.FindVaultCall(current, PactClammHelper.VaultDepositSelector);
            if (vaultDeposit == null) return null;

            var vaultAddress = PactWeightedPoolHelper.GetReceiver(previous2.Tx);
            if (vaultAddress == null) return null;
            if (PactWeightedPoolHelper.GetReceiver(previous1.Tx) != vaultAddress) return null;

            var assetAId = PactClammHelper.GetUintArg(vaultDeposit, 1);
            var assetAAmount = PactClammHelper.GetUintArg(vaultDeposit, 2);
            var assetBId = PactClammHelper.GetUintArg(vaultDeposit, 3);
            var assetBAmount = PactClammHelper.GetUintArg(vaultDeposit, 4);

            // sanity check against the deposits in the group
            var depositedA = PactWeightedPoolHelper.GetAssetId(previous2.Tx);
            var depositedB = PactWeightedPoolHelper.GetAssetId(previous1.Tx);
            if (!((depositedA == assetAId && depositedB == assetBId) || (depositedA == assetBId && depositedB == assetAId))) return null;

            if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
            if (txGroup != null) current.Tx.Group = txGroup;

            var poolAddress = Address.ForApplication(appCallTx.ApplicationId.Value);

            return new Liquidity
            {
                Direction = LiquidityDirection.DepositLiquidity,
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
