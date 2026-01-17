using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using AVMTradeReporter.Model;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors;
using AVMTradeReporter.Processors.Liqudity;
using AVMTradeReporter.Processors.SWAP;
using System;
using System.Text;
using System.Threading;

namespace AVMTradeReporter.Services
{
    public class TransactionProcessor
    {
        private readonly ILogger<TransactionProcessor> _logger;
        public Dictionary<string, ISwapProcessor> swapProcessors = new Dictionary<string, ISwapProcessor>();
        public Dictionary<string, ILiquidityProcessor> liquidityProcessors = new Dictionary<string, ILiquidityProcessor>();

        public TransactionProcessor(ILogger<TransactionProcessor> logger)
        {
            _logger = logger;
            // Initialize swap processors
            var pactSwap = new PactSwapProcessor(_logger);
            swapProcessors.Add(pactSwap.AppArg.ToLower(), pactSwap);
            var biatecSwap = new BiatecSwapProcessor(_logger);
            swapProcessors.Add(biatecSwap.AppArg.ToLower(), biatecSwap);
            var tinySwap = new TinySwapProcessor(_logger);
            swapProcessors.Add(tinySwap.AppArg.ToLower(), tinySwap);

            // Initialize liquidity processors
            var pactLAdd = new PactLiquidityAddProcessor(_logger);
            liquidityProcessors.Add(pactLAdd.AppArg.ToLower(), pactLAdd);

            var tinyLAdd = new TinyLiquidityAddProcessor(_logger);
            liquidityProcessors.Add(tinyLAdd.AppArg.ToLower(), tinyLAdd);

            var biatecLAdd = new BiatecLiquidityAddProcessor(_logger);
            liquidityProcessors.Add(biatecLAdd.AppArg.ToLower(), biatecLAdd);

            var pactLRem = new PactLiquidityRemoveProcessor(_logger);
            liquidityProcessors.Add(pactLRem.AppArg.ToLower(), pactLRem);

            var tinyLRem = new TinyLiquidityRemoveProcessor(_logger);
            liquidityProcessors.Add(tinyLRem.AppArg.ToLower(), tinyLRem);

            var biatecLRem = new BiatecLiquidityRemoveProcessor(_logger);
            liquidityProcessors.Add(biatecLRem.AppArg.ToLower(), biatecLRem);
        }

        public async Task ProcessBlock(CertifiedBlock block, ITradeService tradeService, ILiquidityService liquidityService, CancellationToken cancellationToken)
        {
            try
            {
                Algorand.Algod.Model.Transactions.SignedTransaction? prevTx1 = null;
                Algorand.Algod.Model.Transactions.SignedTransaction? prevTx2 = null;
                if (block.Block?.Transactions != null)
                {
                    ulong index = 0;
                    foreach (var currTx in block.Block.Transactions)
                    {
                        index++;
                        try
                        {
                            currTx.Tx.FillInParamsFromBlockHeader(block.Block);
                            var txId = currTx.Tx.TxID();
                            await this.ProcessTransaction(currTx, prevTx1, prevTx2, block.Block, currTx.Tx.Group, txId, currTx.Tx.Sender, TxState.Confirmed, tradeService, liquidityService, cancellationToken);
                        }
                        catch (Exception exc)
                        {
                            _logger.LogInformation("Error processing transaction {index} in block {block}: {error}", index, block.Block.Round, exc.Message);
                        }
                        prevTx2 = prevTx1;
                        prevTx1 = currTx;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process block {round}", block.Block?.Round);
            }
        }

        public async Task ProcessTransaction(
            Algorand.Algod.Model.Transactions.SignedTransaction current,
            Algorand.Algod.Model.Transactions.SignedTransaction? previous1,
            Algorand.Algod.Model.Transactions.SignedTransaction? previous2,
            Algorand.Algod.Model.Block? block,
            Digest? txGroup,
            string topTxId,
            Address trader,
            TxState tradeState,
            ITradeService tradeService,
            ILiquidityService liquidityService,
            CancellationToken cancellationToken
            )
        {

            if (current.Tx is Algorand.Algod.Model.Transactions.ApplicationNoopTransaction appCallTx)
            {
                if (appCallTx.ApplicationArgs.Count > 0)
                {
                    var firstAppArg = Convert.ToHexString(appCallTx.ApplicationArgs.First()).ToLower();
                    if (swapProcessors.TryGetValue(firstAppArg, out var swapProcessor))
                    {
                        var trade = swapProcessor.GetTrade(current, previous1, block, txGroup, topTxId, trader, tradeState);
                        if (trade != null)
                        {
                            await tradeService.RegisterTrade(trade, cancellationToken);
                        }
                    }
                    if (liquidityProcessors.TryGetValue(firstAppArg, out var liquidityProcessor))
                    {
                        var liqUpdate = liquidityProcessor.GetLiquidityUpdate(current, previous1, previous2, block, txGroup, topTxId, trader, tradeState);
                        if (liqUpdate != null)
                        {
                            await liquidityService.RegisterLiquidity(liqUpdate, cancellationToken);
                        }
                    }
                }
            }
            // inner tx is not null 

            Algorand.Algod.Model.Transactions.SignedTransaction? prevTx1 = null;
            Algorand.Algod.Model.Transactions.SignedTransaction? prevTx2 = null;
            if (current.Detail?.InnerTxns != null)
            {
                foreach (var currTx in current.Detail.InnerTxns)
                {
                    if (block != null) current.Tx.FillInParamsFromBlockHeader(block);
                    if (txGroup != null) current.Tx.Group = txGroup;
                    var txId = current.Tx.TxID();

                    await ProcessTransaction(currTx, prevTx1, prevTx2, block, current.Tx.Group, topTxId, trader, tradeState, tradeService, liquidityService, cancellationToken);
                    prevTx2 = prevTx1;
                    prevTx1 = currTx;
                }
            }
        }


    }
}
