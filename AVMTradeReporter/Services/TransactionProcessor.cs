using Algorand;
using Algorand.Algod.Model.Transactions;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Processors;
using System.Text;

namespace AVMTradeReporter.Services
{
    public class TransactionProcessor
    {
        public TransactionProcessor()
        {
            // Initialize swap processors
            var pact = new PactSwapProcessor();
            swapProcessors.Add(pact.AppArg, pact);
        }

        public Dictionary<string, ISwapProcessor> swapProcessors = new Dictionary<string, ISwapProcessor>();

        public async Task ProcessTransaction(
            Algorand.Algod.Model.Transactions.SignedTransaction current,
            Algorand.Algod.Model.Transactions.SignedTransaction previous,
            Algorand.Algod.Model.Block? block,
            Digest? txGroup,
            string topTxId,
            Address trader,
            TradeState tradeState,
            ITradeService tradeService,
            CancellationToken cancellationToken
            )
        {

            if (current.Tx is Algorand.Algod.Model.Transactions.ApplicationNoopTransaction appCallTx)
            {
                if (appCallTx.ApplicationArgs.Count > 0)
                {
                    var firstAppArg = Convert.ToHexString(appCallTx.ApplicationArgs.First());
                    if (swapProcessors.TryGetValue(firstAppArg, out var swapProcessor))
                    {
                        var trade = swapProcessor.GetTrade(current, previous, block, txGroup, topTxId, trader, tradeState);
                        if (trade != null)
                        {
                            await tradeService.RegisterTrade(trade, cancellationToken);
                        }
                    }
                }
            }
            // inner tx is not null 

            Algorand.Algod.Model.Transactions.SignedTransaction? prevTx = null;
            if (current.Detail?.InnerTxns != null)
            {
                foreach (var currTx in current.Detail.InnerTxns)
                {
                    if (prevTx != null)
                    {
                        if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
                        if (txGroup != null) current.Tx.Group = txGroup;
                        var txId = current.Tx.TxID();

                        await ProcessTransaction(currTx, prevTx, block, current.Tx.Group, topTxId, trader, tradeState, tradeService, cancellationToken);
                    }
                    prevTx = currTx;
                }
            }
        }


    }
}
