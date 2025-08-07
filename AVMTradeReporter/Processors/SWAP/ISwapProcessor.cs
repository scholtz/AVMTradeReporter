using Algorand;
using Algorand.Algod.Model.Transactions;
using AVMTradeReporter.Model.Data;

namespace AVMTradeReporter.Processors.SWAP
{
    public interface ISwapProcessor
    {
        public string AppArg { get; set; }

        public Trade? GetTrade(
            SignedTransaction current,
            SignedTransaction? previous,
            Algorand.Algod.Model.Block? block,
            Digest? txGroup,
            string topTxId,
            Address trader,
            TradeState tradeState
            );
    }
}
