
using Algorand.Algod.Model.Transactions;
using Algorand.Gossip;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace AVMTradeReporter.Services
{
    public class GossipBackgroundService : BackgroundService, ITradeService, ILiquidityService
    {
        private readonly ILogger<GossipBackgroundService> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IOptions<AppConfiguration> _appConfig;
        private readonly TradeRepository _tradeRepository;
        private readonly MemoryCache _tx_cache = new MemoryCache(new MemoryCacheOptions());
        private readonly MemoryCache _tx_group = new MemoryCache(new MemoryCacheOptions());
        private readonly TransactionProcessor _transactionProcessor;
        public GossipBackgroundService(
            ILoggerFactory loggerFactory,
            IOptions<AppConfiguration> appConfig,
            TradeRepository tradeRepository,
            TransactionProcessor transactionProcessor
            )
        {
            _logger = loggerFactory.CreateLogger<GossipBackgroundService>();
            _loggerFactory = loggerFactory;
            _tradeRepository = tradeRepository;
            _transactionProcessor = transactionProcessor;
            _appConfig = appConfig;
        }

        public async Task RegisterTrade(Trade trade, CancellationToken cancellationToken)
        {
            await _tradeRepository.StoreTradesAsync([trade], cancellationToken);
        }
        public Task RegisterLiquidity(Liquidity liquidityUpdate, CancellationToken cancellationToken)
        {
            //throw new NotImplementedException();
            return Task.CompletedTask;
        }
        ConcurrentBag<GossipWebsocketClient> _clients = new ConcurrentBag<GossipWebsocketClient>();
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            foreach (var clientConfig in _appConfig.Value.GossipWebsocketClientConfigurations)
            {
                _logger.LogInformation($"Starting {clientConfig.Host}");
                var client = new GossipWebsocketClient(_loggerFactory.CreateLogger<GossipWebsocketClient>(), clientConfig);
                client.TransactionReceivedEvent += Client1_TransactionReceivedEvent;
                await client.Start();
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000);
            }
        }

        private async Task Client1_TransactionReceivedEvent(object sender, IEnumerable<SignedTransaction> txs)
        {
            var tx = txs.FirstOrDefault();
            if (tx == null) return;

            var txId = tx.Tx.TxID();
            if (_tx_cache.TryGetValue(txId, out _))
            {
                // Transaction already processed
                return;
            }
            _tx_cache.Set(txId, tx, TimeSpan.FromMinutes(10));
            if (tx.Tx.Group != null && tx.Tx.Group.Bytes.Length > 0)
            {
                List<SignedTransaction> txsGroup = txs.ToList();
                _tx_group.Set(tx.Tx.Group.ToString(), txsGroup, TimeSpan.FromMinutes(10));

                Algorand.Algod.Model.Transactions.SignedTransaction? prevTx1 = null;
                Algorand.Algod.Model.Transactions.SignedTransaction? prevTx2 = null;
                var cancellationTokenSource = new CancellationTokenSource();
                if (txsGroup != null)
                {
                    ulong index = 0;
                    foreach (var currTx in txsGroup)
                    {
                        index++;
                        try
                        {
                            //currTx.Tx.FillInParamsFromBlockHeader(block.Block);
                            var currTxId = currTx.Tx.TxID();
                            await _transactionProcessor.ProcessTransaction(currTx, prevTx1, prevTx2, null, currTx.Tx.Group, currTxId, currTx.Tx.Sender, TradeState.TxPool, this, this, cancellationTokenSource.Token);
                        }
                        catch (Exception exc)
                        {
                            _logger.LogInformation("Error processing transaction from gossip {group}: {error}", tx.Tx.Group.ToString(), exc.Message);
                        }
                        prevTx2 = prevTx1;
                        prevTx1 = currTx;
                    }
                }

            }
        }

    }
}
