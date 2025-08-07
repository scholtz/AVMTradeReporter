using Algorand;
using Algorand.Algod.Model.Transactions;
using AVMTradeReporter.Model.Data;

namespace AVMTradeReporter.Processors.Liqudity
{
    public interface ILiquidityProcessor
    {
        public string AppArg { get; set; }

        public Liquidity? GetLiquidityUpdate(
            SignedTransaction current,
            SignedTransaction? previous1,
            SignedTransaction? previous2,
            Algorand.Algod.Model.Block? block,
            Digest? txGroup,
            string topTxId,
            Address liqudityProvider,
            TradeState tradeState
            );
    }
}
