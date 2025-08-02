using Algorand;
using Algorand.Algod;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Indexer.Model;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.Nodes;
using Microsoft.Extensions.Options;
using MsgPack;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AVMTradeReporter.Services
{
    public class TradeReporterBackgroundService : BackgroundService
    {
        private readonly ILogger<TradeReporterBackgroundService> _logger;
        private readonly IOptions<AppConfiguration> _appConfig;
        private readonly Algorand.Algod.IDefaultApi _algod;
        private readonly HttpClient _httpClient;
        private readonly ElasticsearchClient _elasticClient;

        public static Indexer? Indexer { get; set; }

        public TradeReporterBackgroundService(
            ILogger<TradeReporterBackgroundService> logger,
            IOptions<AppConfiguration> appConfig,
            ElasticsearchClient elasticClient
            )
        {
            _logger = logger;
            _appConfig = appConfig;

            _httpClient = HttpClientConfigurator.ConfigureHttpClient(appConfig.Value.Algod.Host, appConfig.Value.Algod.ApiKey, appConfig.Value.Algod.Header);
            _algod = new DefaultApi(_httpClient);
            _elasticClient = elasticClient;

            CreateTradeIndexTemplateAsync().Wait();
#if DEBUG
            Indexer = new Indexer()
            {
                Id = _appConfig.Value.IndexerId,
                Updated = DateTimeOffset.Now,
                Round = _appConfig.Value.StartRound ?? 52337928,
                GenesisId = "mainnet-v1.0"
            };
#else
            var indexerRequest = _elasticClient.Get<Indexer>(new Id(_appConfig.Value.IndexerId));
            if (indexerRequest.IsValidResponse && indexerRequest.Source != null)
            {
                Indexer = indexerRequest.Source;
            }
            else
            {
                // create new indexer
                Indexer = new Indexer()
                {
                    Id = _appConfig.Value.IndexerId,
                    Updated = DateTimeOffset.Now,
                    Round = _appConfig.Value.StartRound ?? 52337928,
                    GenesisId = "mainnet-v1.0"
                };
                var indexResult = _elasticClient.Index(Indexer, new Id(Indexer.Id));
                if (indexResult.IsValidResponse)
                {
                    _logger.LogInformation("Indexer created with ID: {indexerId}", Indexer.Id);
                }
                else
                {
                    _logger.LogError("Failed to create indexer: {error}", indexerRequest.DebugInformation);
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


                    var blockStatus = await _algod.WaitForBlockAsync(stoppingToken, Indexer?.Round ?? throw new Exception("Rund not defined"));
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

        private static readonly byte[] PactSwapAppArg = Encoding.UTF8.GetBytes("SWAP");
        private static readonly byte[] TinySwapAppArg = Encoding.UTF8.GetBytes("swap");
        private static readonly byte[] BiatecSwapAppArg = Convert.FromHexString("2013349e");

        private async Task ProcessTransaction(Algorand.Algod.Model.Transactions.SignedTransaction current, Algorand.Algod.Model.Transactions.SignedTransaction previous, Algorand.Algod.Model.Block block, Digest? txGroup, string topTxId, Address trader, CancellationToken cancellationToken)
        {
            if (current?.Detail?.InnerTxns != null)
            {
                if (current.Tx is Algorand.Algod.Model.Transactions.ApplicationNoopTransaction appCallTx)
                {
                    // pact or tiny
                    if (current.Detail.InnerTxns.Count == 1)
                    {
                        // pact
                        if (appCallTx.ApplicationArgs.Count > 0 && appCallTx.ApplicationArgs.First().AsSpan().SequenceEqual(PactSwapAppArg))
                        {
                            if (previous.Tx is AssetTransferTransaction inAssetTransferTx)
                            {
                                // from asa
                                var inner = current.Detail.InnerTxns.First().Tx;
                                if (inner is AssetTransferTransaction outAssetTransferTx)
                                {
                                    // to asa
                                    current.Tx.FillInParamsFromBlockHeader(block);
                                    if (txGroup != null) current.Tx.Group = txGroup;
                                    var trade = new Trade
                                    {
                                        AssetIdIn = inAssetTransferTx.XferAsset,
                                        AssetIdOut = outAssetTransferTx.XferAsset,
                                        AssetAmountIn = inAssetTransferTx.AssetAmount,
                                        AssetAmountOut = outAssetTransferTx.AssetAmount,
                                        TxId = current.Tx.TxID(),
                                        BlockId = block.Round ?? 0,
                                        TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                        Protocol = DEXProtocol.Pact,
                                        PoolAddress = outAssetTransferTx.Sender.EncodeAsString(),
                                        PoolAppId = appCallTx.ApplicationId ?? 0,
                                        TopTxId = topTxId,
                                        Trader = trader.EncodeAsString(),
                                        TradeState = TradeState.Confirmed
                                    };
                                    await RegisterTrade(trade, cancellationToken);
                                }
                                if (inner is PaymentTransaction outPaymentTx)
                                {
                                    // to native
                                    current.Tx.FillInParamsFromBlockHeader(block);
                                    if (txGroup != null) current.Tx.Group = txGroup;

                                    var trade = new Trade
                                    {
                                        AssetIdIn = inAssetTransferTx.XferAsset,
                                        AssetIdOut = 0,
                                        AssetAmountIn = inAssetTransferTx.AssetAmount,
                                        AssetAmountOut = outPaymentTx.Amount ?? 0,
                                        TxId = current.Tx.TxID(),
                                        BlockId = block.Round ?? 0,
                                        TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                        Protocol = DEXProtocol.Pact,
                                        PoolAddress = outPaymentTx.Sender.EncodeAsString(),
                                        PoolAppId = appCallTx.ApplicationId ?? 0,
                                        TopTxId = topTxId,
                                        Trader = trader.EncodeAsString(),
                                        TradeState = TradeState.Confirmed
                                    };
                                    await RegisterTrade(trade, cancellationToken);
                                }
                            }
                            if (previous.Tx is PaymentTransaction inPayTx)
                            {
                                // from native
                                var inner = current.Detail.InnerTxns.First().Tx;
                                if (inner is AssetTransferTransaction outAssetTransferTx)
                                {
                                    // to asa
                                    current.Tx.FillInParamsFromBlockHeader(block);
                                    if (txGroup != null) current.Tx.Group = txGroup;

                                    var trade = new Trade
                                    {
                                        AssetIdIn = 0,
                                        AssetIdOut = outAssetTransferTx.XferAsset,
                                        AssetAmountIn = inPayTx.Amount ?? 0,
                                        AssetAmountOut = outAssetTransferTx.AssetAmount,
                                        TxId = current.Tx.TxID(),
                                        BlockId = block.Round ?? 0,
                                        TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                        Protocol = DEXProtocol.Pact,
                                        PoolAddress = outAssetTransferTx.Sender.EncodeAsString(),
                                        PoolAppId = appCallTx.ApplicationId ?? 0,
                                        TopTxId = topTxId,
                                        Trader = trader.EncodeAsString(),
                                        TradeState = TradeState.Confirmed
                                    };
                                    await RegisterTrade(trade, cancellationToken);
                                }
                                //if (inner is PaymentTransaction outPaymentTx)
                                //{
                                //    // to native
                                //    current.Tx.FillInParamsFromBlockHeader(block);
                                //    if (txGroup != null) current.Tx.Group = txGroup;
                                //    RegisterTrade(0, 0, inPayTx.Amount ?? 0, outPaymentTx.Amount ?? 0, current.Tx.TxID());
                                //}
                            }
                        }
                        else if (appCallTx.ApplicationArgs.Count > 0 && appCallTx.ApplicationArgs.First().AsSpan().SequenceEqual(TinySwapAppArg))
                        {
                            // tiny
                            if (previous.Tx is AssetTransferTransaction inAssetTransferTx)
                            {
                                // from asa
                                var inner = current.Detail.InnerTxns.First().Tx;
                                if (inner is AssetTransferTransaction outAssetTransferTx)
                                {
                                    // to asa
                                    current.Tx.FillInParamsFromBlockHeader(block);
                                    if (txGroup != null) current.Tx.Group = txGroup;
                                    var trade = new Trade
                                    {
                                        AssetIdIn = inAssetTransferTx.XferAsset,
                                        AssetIdOut = outAssetTransferTx.XferAsset,
                                        AssetAmountIn = inAssetTransferTx.AssetAmount,
                                        AssetAmountOut = outAssetTransferTx.AssetAmount,
                                        TxId = current.Tx.TxID(),
                                        BlockId = block.Round ?? 0,
                                        TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                        Protocol = DEXProtocol.Tiny,
                                        PoolAddress = outAssetTransferTx.Sender.EncodeAsString(),
                                        PoolAppId = appCallTx.ApplicationId ?? 0,
                                        TopTxId = topTxId,
                                        Trader = trader.EncodeAsString(),
                                        TradeState = TradeState.Confirmed
                                    };
                                    await RegisterTrade(trade, cancellationToken);
                                }
                                if (inner is PaymentTransaction outPaymentTx)
                                {
                                    // to native
                                    current.Tx.FillInParamsFromBlockHeader(block);
                                    if (txGroup != null) current.Tx.Group = txGroup;
                                    var trade = new Trade
                                    {
                                        AssetIdIn = inAssetTransferTx.XferAsset,
                                        AssetIdOut = 0,
                                        AssetAmountIn = inAssetTransferTx.AssetAmount,
                                        AssetAmountOut = outPaymentTx.Amount ?? 0,
                                        TxId = current.Tx.TxID(),
                                        BlockId = block.Round ?? 0,
                                        TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                        Protocol = DEXProtocol.Tiny,
                                        PoolAddress = outPaymentTx.Sender.EncodeAsString(),
                                        PoolAppId = appCallTx.ApplicationId ?? 0,
                                        TopTxId = topTxId,
                                        Trader = trader.EncodeAsString(),
                                        TradeState = TradeState.Confirmed
                                    };
                                    await RegisterTrade(trade, cancellationToken);
                                }
                            }
                            if (previous.Tx is PaymentTransaction inPayTx)
                            {
                                // from native
                                var inner = current.Detail.InnerTxns.First().Tx;
                                if (inner is AssetTransferTransaction outAssetTransferTx)
                                {
                                    // to asa
                                    current.Tx.FillInParamsFromBlockHeader(block);
                                    if (txGroup != null) current.Tx.Group = txGroup;
                                    var trade = new Trade
                                    {
                                        AssetIdIn = 0,
                                        AssetIdOut = outAssetTransferTx.XferAsset,
                                        AssetAmountIn = inPayTx.Amount ?? 0,
                                        AssetAmountOut = outAssetTransferTx.AssetAmount,
                                        TxId = current.Tx.TxID(),
                                        BlockId = block.Round ?? 0,
                                        TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                        Protocol = DEXProtocol.Tiny,
                                        PoolAddress = outAssetTransferTx.Sender.EncodeAsString(),
                                        PoolAppId = appCallTx.ApplicationId ?? 0,
                                        TopTxId = topTxId,
                                        Trader = trader.EncodeAsString(),
                                        TradeState = TradeState.Confirmed
                                    };
                                    await RegisterTrade(trade, cancellationToken);
                                }
                                //if (inner is PaymentTransaction outPaymentTx)
                                //{
                                //    // to native
                                //    current.Tx.FillInParamsFromBlockHeader(block);
                                //    if (txGroup != null) current.Tx.Group = txGroup;
                                //    RegisterTrade(0, 0, inPayTx.Amount ?? 0, outPaymentTx.Amount ?? 0, current.Tx.TxID());
                                //}
                            }
                        }
                    }
                    else
                    {
                        // biatec
                        if (appCallTx.ApplicationArgs.Count > 0 && appCallTx.ApplicationArgs.First().AsSpan().SequenceEqual(BiatecSwapAppArg))
                        {
                            if (previous.Tx is AssetTransferTransaction inAssetTransferTx)
                            {
                                // from asa
                                foreach (var inner in current.Detail.InnerTxns)
                                {
                                    // find first axfer or pay tx

                                    if (inner.Tx is AssetTransferTransaction outAssetTransferTx)
                                    {
                                        // to asa
                                        current.Tx.FillInParamsFromBlockHeader(block);
                                        if (txGroup != null) current.Tx.Group = txGroup;
                                        var trade = new Trade
                                        {
                                            AssetIdIn = inAssetTransferTx.XferAsset,
                                            AssetIdOut = outAssetTransferTx.XferAsset,
                                            AssetAmountIn = inAssetTransferTx.AssetAmount,
                                            AssetAmountOut = outAssetTransferTx.AssetAmount,
                                            TxId = current.Tx.TxID(),
                                            BlockId = block.Round ?? 0,
                                            TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                            Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                            Protocol = DEXProtocol.Biatec,
                                            PoolAddress = outAssetTransferTx.Sender.EncodeAsString(),
                                            PoolAppId = appCallTx.ApplicationId ?? 0,
                                            TopTxId = topTxId,
                                            Trader = trader.EncodeAsString(),
                                            TradeState = TradeState.Confirmed
                                        };
                                        await RegisterTrade(trade, cancellationToken);

                                        break;
                                    }
                                    if (inner.Tx is PaymentTransaction outPaymentTx)
                                    {
                                        // to native
                                        current.Tx.FillInParamsFromBlockHeader(block);
                                        if (txGroup != null) current.Tx.Group = txGroup;
                                        var trade = new Trade
                                        {
                                            AssetIdIn = inAssetTransferTx.XferAsset,
                                            AssetIdOut = 0,
                                            AssetAmountIn = inAssetTransferTx.AssetAmount,
                                            AssetAmountOut = outPaymentTx.Amount ?? 0,
                                            TxId = current.Tx.TxID(),
                                            BlockId = block.Round ?? 0,
                                            TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                            Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                            Protocol = DEXProtocol.Biatec,
                                            PoolAddress = outPaymentTx.Sender.EncodeAsString(),
                                            PoolAppId = appCallTx.ApplicationId ?? 0,
                                            TopTxId = topTxId,
                                            Trader = trader.EncodeAsString(),
                                            TradeState = TradeState.Confirmed
                                        };
                                        await RegisterTrade(trade, cancellationToken);
                                        break;
                                    }
                                }
                            }
                            if (previous.Tx is PaymentTransaction inPayTx)
                            {
                                // from native
                                foreach (var inner in current.Detail.InnerTxns)
                                {
                                    // find first axfer or pay tx
                                    if (inner.Tx is AssetTransferTransaction outAssetTransferTx)
                                    {
                                        // to asa
                                        current.Tx.FillInParamsFromBlockHeader(block);
                                        if (txGroup != null) current.Tx.Group = txGroup;
                                        var trade = new Trade
                                        {
                                            AssetIdIn = 0,
                                            AssetIdOut = outAssetTransferTx.XferAsset,
                                            AssetAmountIn = inPayTx.Amount ?? 0,
                                            AssetAmountOut = outAssetTransferTx.AssetAmount,
                                            TxId = current.Tx.TxID(),
                                            BlockId = block.Round ?? 0,
                                            TxGroup = Convert.ToBase64String(current.Tx.Group.Bytes),
                                            Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(block.Timestamp ?? 0)),
                                            Protocol = DEXProtocol.Tiny,
                                            PoolAddress = outAssetTransferTx.Sender.EncodeAsString(),
                                            PoolAppId = appCallTx.ApplicationId ?? 0,
                                            TopTxId = topTxId,
                                            Trader = trader.EncodeAsString(),
                                            TradeState = TradeState.Confirmed
                                        };
                                        await RegisterTrade(trade, cancellationToken);
                                        break;
                                    }
                                }
                                //if (inner is PaymentTransaction outPaymentTx)
                                //{
                                //    // to native
                                //    current.Tx.FillInParamsFromBlockHeader(block);
                                //    if (txGroup != null) current.Tx.Group = txGroup;
                                //    RegisterTrade(0, 0, inPayTx.Amount ?? 0, outPaymentTx.Amount ?? 0, current.Tx.TxID());
                                //}
                            }
                        }
                    }

                }
                // inner tx is not null 

                Algorand.Algod.Model.Transactions.SignedTransaction? prevTx = null;
                foreach (var currTx in current.Detail.InnerTxns)
                {
                    if (prevTx != null)
                    {
                        current.Tx.FillInParamsFromBlockHeader(block);
                        if (txGroup != null) current.Tx.Group = txGroup;
                        var txId = current.Tx.TxID();

                        await ProcessTransaction(currTx, prevTx, block, current.Tx.Group, topTxId, trader, cancellationToken);
                    }
                    prevTx = currTx;
                }
            }
        }


        ConcurrentDictionary<string, Trade> _trades = new ConcurrentDictionary<string, Trade>();
        private async Task RegisterTrade(Trade trade, CancellationToken cancellationToken)
        {
            _trades[trade.TxId] = trade;
            try
            {
                //var response = await _elasticClient.IndexAsync(trade, trade.TxId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to index trade {txId}", trade.TxId);
            }
        }

        private async Task StoreTrades(CancellationToken cancellationToken)
        {
            if (!_trades.Any())
            {
                _logger.LogDebug("No trades to store");
                return;
            }

            try
            {
                var tradesToIndex = _trades.Values.ToList();
                _logger.LogInformation("Bulk indexing {tradeCount} trades", tradesToIndex.Count);

                var bulkRequest = new BulkRequest("trades")
                {
                    Operations = new BulkOperationsCollection()
                };

                foreach (var trade in tradesToIndex)
                {
                    bulkRequest.Operations.Add(new BulkIndexOperation<Trade>(trade)
                    {
                        Id = trade.TxId
                    });
                }

                var bulkResponse = await _elasticClient.BulkAsync(bulkRequest, cancellationToken);

                if (bulkResponse.IsValidResponse)
                {
                    var successCount = bulkResponse.Items.Count(item => item.IsValid);
                    var failureCount = bulkResponse.Items.Count(item => !item.IsValid);

                    _logger.LogInformation("Bulk indexing completed: {successCount} successful, {failureCount} failed",
                        successCount, failureCount);

                    if (failureCount > 0)
                    {
                        foreach (var failedItem in bulkResponse.Items.Where(item => !item.IsValid))
                        {
                            _logger.LogWarning("Failed to index trade {id}: {error}",
                                failedItem.Id, failedItem.Error?.Reason ?? "Unknown error");
                        }
                    }

                    // Clear successfully indexed trades
                    foreach (var item in bulkResponse.Items.Where(item => item.IsValid))
                    {
                        _trades.TryRemove(item.Id, out _);
                    }
                }
                else
                {
                    _logger.LogError("Bulk indexing failed: {error}", bulkResponse.DebugInformation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bulk index trades");
            }
        }
        private async Task IncrementIndexer(CancellationToken cancellationToken)
        {
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
            var indexResult = await _elasticClient.IndexAsync(Indexer, new Id(Indexer.Id), cancellationToken);
            if (indexResult.IsSuccess())
            {
                _logger.LogInformation("Round progressed. Next round: {round}", Indexer.Round);
            }
            else
            {
                _logger.LogError("Failed to update indexer: {error}", indexResult.DebugInformation);
            }
        }
        private async Task ProcessBlockWorkAsync(ulong blockId, CancellationToken cancellationToken)
        {
            try
            {
                var algodConfig = _appConfig.Value.Algod;

                _logger.LogInformation("Loading block {blockId}", blockId);
                var block = await _algod.GetBlockAsync(blockId, null, false);
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
                                await ProcessTransaction(currTx, prevTx, block.Block, currTx.Tx.Group, txId, currTx.Tx.Sender, cancellationToken);
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
                await StoreTrades(cancellationToken);
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

        async Task CreateTradeIndexTemplateAsync()
        {
            var templateRequest = new PutIndexTemplateRequest
            {
                Name = "trades_template",          // Name of the index template
                IndexPatterns = new[] { "trades-*" },    // Pattern to match indexes
                DataStream = new DataStreamVisibility(),
                Template = new IndexTemplateMapping
                {
                    Mappings = new TypeMapping
                    {
                        Properties = new Properties
                        {
                            { "assetIdIn", new LongNumberProperty () },
                            { "assetIdOut", new LongNumberProperty () },
                            { "assetAmountIn", new LongNumberProperty () },
                            { "assetAmountOut", new LongNumberProperty () },
                            { "txId", new KeywordProperty() },
                            { "blockId", new LongNumberProperty() },
                            { "txGroup", new KeywordProperty() },
                            { "timestamp", new DateProperty() },
                            { "protocol", new KeywordProperty() },      // Enum as keyword
                            { "trader", new KeywordProperty() },
                            { "poolAddress", new KeywordProperty() },
                            { "poolAppId", new LongNumberProperty() },
                            { "topTxId", new KeywordProperty() },
                            { "tradeState", new KeywordProperty() }    // Enum as keyword
                        }
                    }
                }
            };

            var response = await _elasticClient.Indices.PutIndexTemplateAsync(templateRequest);

            Console.WriteLine($"Template created: {response.IsValidResponse}");
        }

    }
}