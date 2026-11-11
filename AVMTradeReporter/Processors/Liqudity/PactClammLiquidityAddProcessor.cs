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
    /// Group layout: [pay|axfer A deposit to vault] [pay|axfer B deposit to vault] [appl pool mint(tick_low, tick_high, ..)]
    /// (one or both deposits; mainnet mints use the 4d17cf3e selector, see PactClammHelper.MintV2Selector)
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
            if (appCallTx.ApplicationId == null || appCallTx.ApplicationId == 0) return null;

            var vaultDeposit = PactClammHelper.FindVaultCall(current, PactClammHelper.VaultDepositSelector);
            if (vaultDeposit?.ApplicationId == null) return null;

            var assetAId = PactClammHelper.GetUintArg(vaultDeposit, 1);
            var assetAAmount = PactClammHelper.GetUintArg(vaultDeposit, 2);
            var assetBId = PactClammHelper.GetUintArg(vaultDeposit, 3);
            var assetBAmount = PactClammHelper.GetUintArg(vaultDeposit, 4);
            if (assetAId == assetBId) return null;

            // sanity check against the deposits in the group: the transfers preceding the call that pay the vault (one or
            // two - a position entirely on one side of the price needs only one asset) must be of the pool's assets
            var vaultAddress = Address.ForApplication(vaultDeposit.ApplicationId.Value);
            // previous1 / previous2 are the preceding transactions of the block: only those of this very group count
            var group = txGroup ?? current.Tx.Group;
            var deposits = new[] { previous1, previous2 }
                .Where(p => p?.Tx is AssetTransferTransaction or PaymentTransaction)
                .Where(p => group == null || p!.Tx.Group == null || p.Tx.Group.Bytes.SequenceEqual(group.Bytes)) // inner txns carry no group
                .Where(p => PactWeightedPoolHelper.GetReceiver(p!.Tx) is { } receiver && receiver == vaultAddress)
                .ToList();
            if (deposits.Count == 0) return null;
            if (deposits.Any(p => PactWeightedPoolHelper.GetAssetId(p!.Tx) is var asset && asset != assetAId && asset != assetBId)) return null;

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
