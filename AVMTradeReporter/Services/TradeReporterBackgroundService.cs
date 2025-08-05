using Algorand;
using Algorand.Algod;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using AVMIndexReporter.Repository;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace AVMTradeReporter.Services
{
    public class TradeReporterBackgroundService : BackgroundService, ITradeService
    {
        private readonly ILogger<TradeReporterBackgroundService> _logger;
        private readonly IOptions<AppConfiguration> _appConfig;
        private readonly Algorand.Algod.IDefaultApi _algod;
        private readonly HttpClient _httpClient;
        private readonly IndexerRepository _indexerRepository;
        private readonly TradeRepository _tradeRepository;
        private readonly TransactionProcessor _transactionProcessor;


        public static Indexer? Indexer { get; set; }

        public TradeReporterBackgroundService(
            ILogger<TradeReporterBackgroundService> logger,
            IOptions<AppConfiguration> appConfig,
            IndexerRepository indexerRepository,
            TradeRepository tradeRepository,
            TransactionProcessor transactionProcessor
            )
        {
            _logger = logger;
            _appConfig = appConfig;
            _indexerRepository = indexerRepository;
            _tradeRepository = tradeRepository;
            _transactionProcessor = transactionProcessor;

            _httpClient = HttpClientConfigurator.ConfigureHttpClient(appConfig.Value.Algod.Host, appConfig.Value.Algod.ApiKey, appConfig.Value.Algod.Header);
            _algod = new DefaultApi(_httpClient);


#if DEBUG
            Indexer = new Indexer()
            {
                Id = _appConfig.Value.IndexerId,
                Updated = DateTimeOffset.Now,
                Round = _appConfig.Value.StartRound ?? 52337928,
                GenesisId = "mainnet-v1.0"
            };
#else
            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Indexer = _indexerRepository.GetIndexerAsync(_appConfig.Value.IndexerId, cancellationTokenSource.Token).Result;
            if (Indexer == null)
            {
                // create new indexer
                Indexer = new Indexer()
                {
                    Id = _appConfig.Value.IndexerId,
                    Updated = DateTimeOffset.Now,
                    Round = _appConfig.Value.StartRound ?? 52337928,
                    GenesisId = "mainnet-v1.0"
                };
                var success = indexerRepository.StoreIndexerAsync(Indexer, cancellationTokenSource.Token).Result;
                if (success)
                {
                    _logger.LogInformation("Indexer created with ID: {indexerId}", Indexer.Id);
                }
                else
                {
                    _logger.LogError("Failed to create indexer");
                }
            }
#endif
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Trade Reporter Background Service starting...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (Indexer == null)
                    {
                        await Task.Delay(1000);
                        continue;
                    }
                    //await ProcessBlockWorkAsync(52243617, stoppingToken);// pact
                    //await ProcessBlockWorkAsync(52279620, stoppingToken);// biatec
                    //await ProcessBlockWorkAsync(52335125, stoppingToken);//tiny

                    if (_appConfig.Value.MinRound != null && _appConfig.Value.MinRound > Indexer.Round)
                    {
                        _logger.LogInformation("Min round reached");
                        await Task.Delay(TimeSpan.FromMinutes(10));
                        continue;
                    }
                    if (_appConfig.Value.MaxRound != null && _appConfig.Value.MaxRound < Indexer.Round)
                    {
                        _logger.LogInformation("Max round reached");
                        await Task.Delay(TimeSpan.FromMinutes(10));
                        continue;
                    }

#if !DEBUG
                    var blockStatus = await _algod.WaitForBlockAsync(stoppingToken, Indexer?.Round ?? throw new Exception("Rund not defined"));
#endif
                    await ProcessBlockWorkAsync(Indexer.Round, stoppingToken);
                    await IncrementIndexer(stoppingToken);

                    if (_appConfig.Value.DelayMs.HasValue && _appConfig.Value.DelayMs > 0)
                    {
                        await Task.Delay(_appConfig.Value.DelayMs.Value);
                    }
#if DEBUG
                    return;
#endif
                    // 
                    //await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Run every 5 minutes
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Trade Reporter Background Service was cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred in Trade Reporter Background Service.");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Wait before retrying
                }
            }

            _logger.LogInformation("Trade Reporter Background Service stopped.");
        }

        
        
        ConcurrentDictionary<string, Trade> _trades = new ConcurrentDictionary<string, Trade>();
        private async Task RegisterTrade(Trade trade, CancellationToken cancellationToken)
        {
            _trades[trade.TxId] = trade;
        }


        private async Task IncrementIndexer(CancellationToken cancellationToken)
        {
#if DEBUG
            return;
#endif
            if (Indexer == null) return;
            if (_appConfig.Value.Direction == "-")
            {
                Indexer.Round = Indexer.Round - 1;
            }
            else
            {
                Indexer.Round = Indexer.Round + 1;
            }
            Indexer.Updated = DateTimeOffset.Now;
            _indexerRepository.StoreIndexerAsync(Indexer, cancellationToken).Wait(cancellationToken);
        }
        private async Task ProcessBlockWorkAsync(ulong blockId, CancellationToken cancellationToken)
        {
            try
            {
                var algodConfig = _appConfig.Value.Algod;

                _logger.LogInformation("Loading block {blockId}", blockId);
                var block = await _algod.GetBlockAsync(blockId, Format.Msgpack, false);
                _logger.LogInformation("Found transactions: {txCount}", block.Block?.Transactions?.Count);
                Algorand.Algod.Model.Transactions.SignedTransaction? prevTx = null;
                if (block.Block?.Transactions != null)
                {
                    ulong index = 0;
                    foreach (var currTx in block.Block.Transactions)
                    {
                        index++;
                        try
                        {
                            if (prevTx != null)
                            {
                                currTx.Tx.FillInParamsFromBlockHeader(block.Block);
                                var txId = currTx.Tx.TxID();
                                await _transactionProcessor.ProcessTransaction(currTx, prevTx, block.Block, currTx.Tx.Group, txId, currTx.Tx.Sender, TradeState.Confirmed, this, cancellationToken);
                            }
                        }
                        catch (Exception exc)
                        {
                            _logger.LogInformation("Error processing transaction {index} in block {block}: {error}", index, block.Block.Round, exc.Message);
                        }
                        prevTx = currTx;
                    }
                }
                //var tx = block.Block.Transactions.FirstOrDefault();
                //var id = tx?.Tx.TxID();
                _tradeRepository.StoreTradesAsync(_trades.Values.ToArray(), cancellationToken).Wait(cancellationToken);
                await Task.CompletedTask; // Placeholder for actual work
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Trade Reporter Background Service is stopping...");
            await base.StopAsync(stoppingToken);
        }

        Task ITradeService.RegisterTrade(Trade trade, CancellationToken cancellationToken)
        {
            return RegisterTrade(trade, cancellationToken);
        }
    }
}