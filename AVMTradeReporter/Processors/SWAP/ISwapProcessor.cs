using Algorand;
using Algorand.Algod.Model.Transactions;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;

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
            TxState tradeState
            );
    }
}
