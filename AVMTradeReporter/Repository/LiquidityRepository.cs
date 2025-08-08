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
        private readonly PoolRepository _poolRepository;

        public LiquidityRepository(
            ElasticsearchClient elasticClient,
            ILogger<LiquidityRepository> logger,
            IHubContext<BiatecScanHub> hubContext,
            PoolRepository poolRepository
            )
        {
            _elasticClient = elasticClient;
            _logger = logger;
            _hubContext = hubContext;
            _poolRepository = poolRepository;
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

                    // Update pools for successfully stored liquidity updates
                    if (successCount > 0)
                    {
                        var successfulLiquidityUpdates = new List<Liquidity>();
                        var bulkResponseItems = bulkResponse.Items.ToList();
                        
                        for (int i = 0; i < items.Length && i < bulkResponseItems.Count; i++)
                        {
                            if (bulkResponseItems[i].IsValid)
                            {
                                successfulLiquidityUpdates.Add(items[i]);
                            }
                        }

                        // Update pools from confirmed liquidity updates in background
                        _ = Task.Run(async () =>
                        {
                            foreach (var liquidity in successfulLiquidityUpdates)
                            {
                                await _poolRepository.UpdatePoolFromLiquidity(liquidity, cancellationToken);
                            }
                        }, cancellationToken);
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

        private async Task PublishLiquidityUpdatesToHub(Liquidity[] liquidityUpdates, CancellationToken cancellationToken)
        {
            try
            {
                var subscriptions = BiatecScanHub.GetSubscriptions();

                if (!subscriptions.Any())
                {
                    _logger.LogDebug("No active subscriptions, skipping liquidity update publication");
                    return;
                }

                foreach (var liquidityUpdate in liquidityUpdates)
                {
                    // Publish to all clients by default
                    await _hubContext.Clients.All.SendAsync("LiquidityUpdated", liquidityUpdate, cancellationToken);

                    // Also send filtered liquidity updates to specific users based on their subscriptions
                    foreach (var subscription in subscriptions)
                    {
                        var userId = subscription.Key;
                        var filter = subscription.Value;

                        if (BiatecScanHub.ShouldSendLiquidityToUser(liquidityUpdate, filter))
                        {
                            await _hubContext.Clients.User(userId).SendAsync("FilteredLiquidityUpdated", liquidityUpdate, cancellationToken);
                        }
                    }
                }

                _logger.LogInformation("Published {liquidityCount} liquidity updates to SignalR hub", liquidityUpdates.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish liquidity updates to SignalR hub");
            }
        }
    }
}
