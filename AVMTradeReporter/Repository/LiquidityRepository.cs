using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.Security;
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

            CreateLiquidityIndexTemplateAsync().Wait();
        }

        private async Task CreateLiquidityIndexTemplateAsync()
        {
            var templateRequest = new Elastic.Clients.Elasticsearch.IndexManagement.PutIndexTemplateRequest
            {
                Name = "liquidity_template",
                IndexPatterns = new[] { "liquidity" },
                Template = new Elastic.Clients.Elasticsearch.IndexManagement.IndexTemplateMapping
                {
                    Mappings = new Elastic.Clients.Elasticsearch.Mapping.TypeMapping
                    {
                        Properties = new Elastic.Clients.Elasticsearch.Mapping.Properties
                        {
                            { "direction", new Elastic.Clients.Elasticsearch.Mapping.KeywordProperty() },
                            { "assetIdA", new Elastic.Clients.Elasticsearch.Mapping.LongNumberProperty() },
                            { "assetIdB", new Elastic.Clients.Elasticsearch.Mapping.LongNumberProperty() },
                            { "assetIdLP", new Elastic.Clients.Elasticsearch.Mapping.LongNumberProperty() },
                            { "assetAmountA", new Elastic.Clients.Elasticsearch.Mapping.LongNumberProperty() },
                            { "assetAmountB", new Elastic.Clients.Elasticsearch.Mapping.LongNumberProperty() },
                            { "assetAmountLP", new Elastic.Clients.Elasticsearch.Mapping.LongNumberProperty() },
                            { "valueUSD", new Elastic.Clients.Elasticsearch.Mapping.DoubleNumberProperty() },
                            { "txId", new Elastic.Clients.Elasticsearch.Mapping.KeywordProperty() },
                            { "blockId", new Elastic.Clients.Elasticsearch.Mapping.LongNumberProperty() },
                            { "txGroup", new Elastic.Clients.Elasticsearch.Mapping.KeywordProperty() },
                            { "timestamp", new Elastic.Clients.Elasticsearch.Mapping.DateProperty() },
                            { "protocol", new Elastic.Clients.Elasticsearch.Mapping.KeywordProperty() },
                            { "liquidityProvider", new Elastic.Clients.Elasticsearch.Mapping.KeywordProperty() },
                            { "poolAddress", new Elastic.Clients.Elasticsearch.Mapping.KeywordProperty() },
                            { "poolAppId", new Elastic.Clients.Elasticsearch.Mapping.LongNumberProperty() },
                            { "topTxId", new Elastic.Clients.Elasticsearch.Mapping.KeywordProperty() },
                            { "txnIndex", new Elastic.Clients.Elasticsearch.Mapping.LongNumberProperty() },
                            { "eventIndex", new Elastic.Clients.Elasticsearch.Mapping.LongNumberProperty() },
                            { "txState", new Elastic.Clients.Elasticsearch.Mapping.KeywordProperty() }
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

        /// <summary>
        /// Persists the liquidity events (upsert by tx id) and reports, per document, what happened - see <see cref="StoreResult"/>.
        /// </summary>
        /// <param name="items">Documents to upsert.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="publish">False when the documents were already announced to the live feed by an earlier attempt.</param>
        public async Task<StoreResult> StoreLiquidityUpdatesAsync(Liquidity[] items, CancellationToken cancellationToken, bool publish = true)
        {
            if (!items.Any())
            {
                return StoreResult.AllStored;
            }

            try
            {
                // Only a document's first store is announced: a document re-sent by a later flush (rejected before, or
                // Elasticsearch unreachable) must not reach the live feed twice.
                if (publish)
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
                }

                _logger.LogInformation("Bulk indexing {count} liquidity updates", items.Length);

                var bulkRequest = new BulkRequest("liquidity")
                {
                    Operations = new BulkOperationsCollection(),
                    // searchable before the block counts as indexed (latest-block): no timer guessing the refresh interval
                    Refresh = Elastic.Clients.Elasticsearch.Refresh.WaitFor,
                };

                foreach (var item in items)
                {
                    // A mempool preview (TxPool) is create-only: it must never overwrite the confirmed document the block
                    // processor may have stored meanwhile (Elasticsearch then answers 409 = exists already, not a rejection).
                    if (item.TxState == TxState.TxPool)
                    {
                        bulkRequest.Operations.Add(new BulkCreateOperation<Liquidity>(item) { Id = item.TxId });
                    }
                    else
                    {
                        bulkRequest.Operations.Add(new BulkIndexOperation<Liquidity>(item) { Id = item.TxId });
                    }
                }
                if (_elasticClient == null)
                {
                    // Update pools from confirmed liquidity updates in background
                    _ = Task.Run(async () =>
                    {
                        foreach (var liquidity in items)
                        {
                            await _poolRepository.UpdatePoolFromLiquidity(liquidity, cancellationToken);
                        }
                    }, cancellationToken);
                    return StoreResult.AllStored; // nothing to persist without Elasticsearch - the events were processed
                }
                else
                {
                    var bulkResponse = await _elasticClient.BulkAsync(bulkRequest, cancellationToken);

                    if (bulkResponse.IsValidResponse)
                    {
                        var successCount = bulkResponse.Items.Count(item => item.IsValid);
                        var failureCount = bulkResponse.Items.Count(item => !item.IsValid);

                        _logger.LogInformation("LP Bulk indexing completed: {successCount} successful, {failureCount} failed",
                            successCount, failureCount);

                        if (failureCount > 0)
                        {
                            foreach (var failedItem in bulkResponse.Items.Where(item => !item.IsValid && item.Status != 409))
                            {
                                _logger.LogWarning("Failed to index liquidity {id}: {error}",
                                    failedItem.Id, failedItem.Error?.Reason ?? "Unknown error");
                            }
                        }

                        var successfulLiquidityUpdates = new List<Liquidity>();
                        var rejectedIds = new List<string>();
                        var bulkResponseItems = bulkResponse.Items.ToList();

                        for (int i = 0; i < items.Length && i < bulkResponseItems.Count; i++)
                        {
                            if (bulkResponseItems[i].IsValid)
                            {
                                successfulLiquidityUpdates.Add(items[i]);
                            }
                            else if (bulkResponseItems[i].Status == 409)
                            {
                                // create-only preview of a document that exists already (confirmed, or an earlier preview): settled
                            }
                            else
                            {
                                rejectedIds.Add(items[i].TxId);
                            }
                        }

                        // Update pools from confirmed liquidity updates in background
                        if (successfulLiquidityUpdates.Count > 0)
                        {
                            _ = Task.Run(async () =>
                            {
                                foreach (var liquidity in successfulLiquidityUpdates)
                                {
                                    await _poolRepository.UpdatePoolFromLiquidity(liquidity, cancellationToken);
                                }
                            }, cancellationToken);
                        }

                        // Per document: the caller retries only the rejected ones (upserts by tx id are idempotent).
                        return new StoreResult(true, rejectedIds);
                    }
                    else
                    {
                        _logger.LogError("LP Bulk indexing failed: {error}", bulkResponse.DebugInformation);
                        return StoreResult.Unreachable;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bulk index LP");
                return StoreResult.Unreachable;
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
                    var subscribedClientsConnections = new HashSet<string>();
                    // Also send filtered liquidity updates to specific users based on their subscriptions
                    foreach (var subscription in subscriptions)
                    {
                        var userId = subscription.Key;
                        var filter = subscription.Value;

                        if (BiatecScanHub.ShouldSendLiquidityToUser(liquidityUpdate, filter))
                        {
                            subscribedClientsConnections.Add(userId);
                        }
                    }
                    await _hubContext.Clients.Users(subscribedClientsConnections).SendAsync(BiatecScanHub.Subscriptions.LIQUIDITY, liquidityUpdate, cancellationToken);
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
