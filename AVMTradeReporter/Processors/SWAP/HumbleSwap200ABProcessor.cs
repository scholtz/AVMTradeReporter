using Algorand;
using Algorand.Algod.Model.Transactions;
using AVM.ClientGenerator.ABI.ARC4;
using AVM.ClientGenerator.Core;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using System.Threading;

namespace AVMTradeReporter.Processors.SWAP
{
    public class HumbleSwap200ABProcessor : ISwapProcessor
    {
        public string AppArg { get; set; } = Convert.ToHexString(Utils.ToARC4MethodSelector("Trader_swapAForB(byte,uint256,uint256)(uint256,uint256)")).ToLower();

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

                //var appEvent = appCallTx.Logs?.LastOrDefault(log => log.StartsWith("Ev_swapAForB"));


                //var trade = new Trade
                //{
                //    AssetIdIn = AssetIdIn,
                //    AssetIdOut = AssetIdOut,
                //    AssetAmountIn = AssetAmountIn,
                //    AssetAmountOut = AssetAmountOut,
                //    TxId = current.Tx.TxID(),
                //    BlockId = block?.Round ?? 0,
                //    TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                //    Timestamp = block == null ? DateTimeOffset.UtcNow : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block?.Timestamp ?? 0)),
                //    Protocol = DEXProtocol.Pact,
                //    PoolAddress = poolAddress.EncodeAsString(),
                //    PoolAppId = appCallTx.ApplicationId ?? 0,
                //    TopTxId = topTxId,
                //    Trader = trader.EncodeAsString(),
                //    TradeState = tradeState,
                //    A = A,
                //    B = B,
                //    L = L
                //};
                //return trade;
            }
            return null;
        }
    }
}
