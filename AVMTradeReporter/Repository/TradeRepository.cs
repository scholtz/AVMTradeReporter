using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.Security;
using Microsoft.AspNetCore.SignalR;

namespace AVMTradeReporter.Repository
{
    public class TradeRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<TradeRepository> _logger;
        private readonly IHubContext<BiatecScanHub> _hubContext;
        private readonly PoolRepository _poolRepository;

        public TradeRepository(
            ElasticsearchClient elasticClient,
            ILogger<TradeRepository> logger,
            IHubContext<BiatecScanHub> hubContext,
            PoolRepository poolRepository
            )
        {
            _elasticClient = elasticClient;
            _logger = logger;
            _hubContext = hubContext;
            _poolRepository = poolRepository;
            CreateTradeIndexTemplateAsync().Wait();
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
            if (_elasticClient == null)
            {
                _logger.LogError("Elasticsearch client is not initialized");
                return;
            }
            var response = await _elasticClient.Indices.PutIndexTemplateAsync(templateRequest);

            Console.WriteLine($"Template created: {response.IsValidResponse}");
        }
        public async Task<bool> StoreTradesAsync(Trade[] trades, CancellationToken cancellationToken)
        {
            if (!trades.Any())
            {
                _logger.LogDebug("No trades to store");
                return false;
            }

            try
            {
                _ = Task.Run(() => PublishTradesToHub(trades, cancellationToken));

                foreach (var item in trades)
                {
                    BiatecScanHub.RecentTrades.Enqueue(item);
                    if (BiatecScanHub.RecentTrades.Count > 100)
                    {
                        BiatecScanHub.RecentTrades.TryDequeue(out _);
                    }
                }

                _logger.LogInformation("Bulk indexing {tradeCount} trades", trades.Length);

                var bulkRequest = new BulkRequest("trades")
                {
                    Operations = new BulkOperationsCollection()
                };

                foreach (var trade in trades)
                {
                    bulkRequest.Operations.Add(new BulkIndexOperation<Trade>(trade)
                    {
                        Id = trade.TxId
                    });
                }
                if (_elasticClient == null)
                {
                    // Update pools from confirmed trades in background
                    _ = Task.Run(async () =>
                    {
                        foreach (var trade in trades)
                        {
                            await _poolRepository.UpdatePoolFromTrade(trade, cancellationToken);
                        }
                    }, cancellationToken);
                    return false;
                }
                else
                {
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

                        // Update pools for successfully stored trades
                        if (successCount > 0)
                        {
                            var successfulTrades = new List<Trade>();
                            var bulkResponseItems = bulkResponse.Items.ToList();

                            for (int i = 0; i < trades.Length && i < bulkResponseItems.Count; i++)
                            {
                                if (bulkResponseItems[i].IsValid)
                                {
                                    successfulTrades.Add(trades[i]);
                                }
                            }

                            // Update pools from confirmed trades in background
                            _ = Task.Run(async () =>
                            {
                                foreach (var trade in successfulTrades)
                                {
                                    await _poolRepository.UpdatePoolFromTrade(trade, cancellationToken);
                                }
                            }, cancellationToken);
                        }

                        return true;
                    }
                    else
                    {
                        _logger.LogError("Bulk indexing failed: {error}", bulkResponse.DebugInformation);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bulk index trades");
                return false;
            }
        }

        private async Task PublishTradesToHub(Trade[] trades, CancellationToken cancellationToken)
        {
            try
            {
                var subscriptions = BiatecScanHub.GetSubscriptions();

                if (!subscriptions.Any())
                {
                    _logger.LogDebug("No active subscriptions, skipping trade publication");
                    return;
                }

                foreach (var trade in trades)
                {
                    var subscribedClientsConnections = new HashSet<string>();

                    // Also send filtered trades to specific users based on their subscriptions
                    foreach (var subscription in subscriptions)
                    {
                        var userId = subscription.Key;
                        var filter = subscription.Value;

                        if (BiatecScanHub.ShouldSendTradeToUser(trade, filter))
                        {
                            subscribedClientsConnections.Add(userId);
                        }
                    }
                    await _hubContext.Clients.Users(subscribedClientsConnections).SendAsync(BiatecScanHub.Subscriptions.TRADE, trade, cancellationToken);
                }

                _logger.LogInformation("Published {tradeCount} trades to SignalR hub", trades.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish trades to SignalR hub");
            }
        }


    }
}
