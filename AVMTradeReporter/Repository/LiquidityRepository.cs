using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Microsoft.AspNetCore.SignalR;

namespace AVMTradeReporter.Repository
{
    public class LiquidityRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<LiquidityRepository> _logger;
        private readonly IHubContext<BiatecScanHub> _hubContext;

        public LiquidityRepository(
            ElasticsearchClient elasticClient,
            ILogger<LiquidityRepository> logger,
            IHubContext<BiatecScanHub> hubContext
            )
        {
            _elasticClient = elasticClient;
            _logger = logger;
            _hubContext = hubContext;
        }
        public async Task<bool> StoreLiquidityUpdatesAsync(Liquidity[] items, CancellationToken cancellationToken)
        {
            if (!items.Any())
            {
                _logger.LogDebug("No items to store");
                return false;
            }

            try
            {
                _ = Task.Run(() => PublishLiquidityUpdatesToHub(items, cancellationToken));

                foreach (var item in items)
                {
                    BiatecScanHub.RecentLiquidityUpdates.Enqueue(item);
                    if (BiatecScanHub.RecentLiquidityUpdates.Count > 100)
                    {
                        BiatecScanHub.RecentLiquidityUpdates.TryDequeue(out _);
                    }
                }

                _logger.LogInformation("Bulk indexing {count} liquidity updates", items.Length);

                var bulkRequest = new BulkRequest("liquidity")
                {
                    Operations = new BulkOperationsCollection()
                };

                foreach (var item in items)
                {
                    bulkRequest.Operations.Add(new BulkIndexOperation<Liquidity>(item)
                    {
                        Id = item.TxId
                    });
                }

                var bulkResponse = await _elasticClient.BulkAsync(bulkRequest, cancellationToken);

                if (bulkResponse.IsValidResponse)
                {
                    var successCount = bulkResponse.Items.Count(item => item.IsValid);
                    var failureCount = bulkResponse.Items.Count(item => !item.IsValid);

                    _logger.LogInformation("LP Bulk indexing completed: {successCount} successful, {failureCount} failed",
                        successCount, failureCount);

                    if (failureCount > 0)
                    {
                        foreach (var failedItem in bulkResponse.Items.Where(item => !item.IsValid))
                        {
                            _logger.LogWarning("Failed to index liquidity {id}: {error}",
                                failedItem.Id, failedItem.Error?.Reason ?? "Unknown error");
                        }
                    }
                    return true;
                }
                else
                {
                    _logger.LogError("LP Bulk indexing failed: {error}", bulkResponse.DebugInformation);
                    return false;
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bulk index LP");
                return false;
            }
        }

        private async Task PublishLiquidityUpdatesToHub(Liquidity[] items, CancellationToken cancellationToken)
        {
            try
            {
                var subscriptions = BiatecScanHub.GetSubscriptions();

                if (!subscriptions.Any())
                {
                    _logger.LogDebug("No active subscriptions, skipping trade publication");
                    return;
                }

                foreach (var item in items)
                {
                    // Publish to all clients by default
                    await _hubContext.Clients.All.SendAsync("LiquidityUpdated", item, cancellationToken);

                    // Also send filtered trades to specific users based on their subscriptions
                    foreach (var subscription in subscriptions)
                    {
                        var userId = subscription.Key;
                        var filter = subscription.Value;

                        if (BiatecScanHub.ShouldSendLiquidityToUser(item, filter))
                        {
                            await _hubContext.Clients.User(userId).SendAsync("FilteredLiquidityUpdated", item, cancellationToken);
                        }
                    }
                }

                _logger.LogInformation("Published {tradeCount} trades to SignalR hub", items.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish trades to SignalR hub");
            }
        }
    }
}
