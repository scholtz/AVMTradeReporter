using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Microsoft.AspNetCore.SignalR;

namespace AVMTradeReporter.Repository
{
    public class TradeRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<TradeRepository> _logger;
        private readonly IHubContext<BiatecScanHub> _hubContext;

        public TradeRepository(
            ElasticsearchClient elasticClient,
            ILogger<TradeRepository> logger,
            IHubContext<BiatecScanHub> hubContext
            )
        {
            _elasticClient = elasticClient;
            _logger = logger;
            _hubContext = hubContext;
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

            var response = await _elasticClient.Indices.PutIndexTemplateAsync(templateRequest);

            Console.WriteLine($"Template created: {response.IsValidResponse}");
        }
        public async Task StoreTradesAsync(Trade[] trades, CancellationToken cancellationToken)
        {
            if (!trades.Any())
            {
                _logger.LogDebug("No trades to store");
                return;
            }

            try
            {
                _ = Task.Run(() => PublishTradesToHub(trades, cancellationToken));

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

                    // Publish successfully stored trades to SignalR hub
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
                    // Publish to all clients by default
                    await _hubContext.Clients.All.SendAsync("TradeUpdated", trade, cancellationToken);

                    // Also send filtered trades to specific users based on their subscriptions
                    foreach (var subscription in subscriptions)
                    {
                        var userId = subscription.Key;
                        var filter = subscription.Value;

                        if (ShouldSendTradeToUser(trade, filter))
                        {
                            await _hubContext.Clients.User(userId).SendAsync("FilteredTradeUpdated", trade, cancellationToken);
                        }
                    }
                }

                _logger.LogInformation("Published {tradeCount} trades to SignalR hub", trades.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish trades to SignalR hub");
            }
        }

        private bool ShouldSendTradeToUser(Trade trade, string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return true; // No filter means send all trades
            }

            try
            {
                // Simple filtering logic - can be enhanced based on requirements
                // Filter format examples:
                // "protocol:Biatec" - filter by protocol
                // "asset:123" - filter by asset ID (either in or out)
                // "trader:ADDR123" - filter by trader address
                // "pool:456" - filter by pool app ID

                var filterParts = filter.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (filterParts.Length != 2)
                {
                    return true; // Invalid filter format, send all
                }

                var filterType = filterParts[0].ToLowerInvariant();
                var filterValue = filterParts[1];

                return filterType switch
                {
                    "protocol" => trade.Protocol.ToString().Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    "asset" => trade.AssetIdIn.ToString() == filterValue || trade.AssetIdOut.ToString() == filterValue,
                    "trader" => trade.Trader.Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    "pool" => trade.PoolAppId.ToString() == filterValue,
                    "pooladdress" => trade.PoolAddress.Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    "state" => trade.TradeState.ToString().Equals(filterValue, StringComparison.OrdinalIgnoreCase),
                    _ => true // Unknown filter type, send all
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error evaluating filter '{filter}' for trade {tradeId}", filter, trade.TxId);
                return true; // On error, send the trade
            }
        }
    }
}
