using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.Nodes;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace AVMTradeReporter.Repository
{
    public class AggregatedPoolRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<AggregatedPoolRepository> _logger;
        private readonly IHubContext<BiatecScanHub> _hubContext;

        private readonly ConcurrentDictionary<(ulong A, ulong B), AggregatedPool> _cache = new();

        public AggregatedPoolRepository(
            ElasticsearchClient elasticClient,
            ILogger<AggregatedPoolRepository> logger,
            IHubContext<BiatecScanHub> hubContext)
        {
            _elasticClient = elasticClient;
            _logger = logger;
            _hubContext = hubContext;

            CreateIndexTemplateAsync().Wait();
        }

        private async Task CreateIndexTemplateAsync()
        {
            var templateRequest = new PutIndexTemplateRequest
            {
                Name = "aggregatedpools_template",
                IndexPatterns = new[] { "aggregatedpools-*" },
                DataStream = new DataStreamVisibility(),
                Template = new IndexTemplateMapping
                {
                    Mappings = new TypeMapping
                    {
                        Properties = new Properties
                        {
                            { "assetIdA", new LongNumberProperty() },
                            { "assetIdB", new LongNumberProperty() },
                            { "a", new LongNumberProperty() },
                            { "b", new LongNumberProperty() },
                            { "poolCount", new IntegerNumberProperty() },
                            { "lastUpdated", new DateProperty() }
                        }
                    }
                }
            };

            var response = await _elasticClient.Indices.PutIndexTemplateAsync(templateRequest);
            _logger.LogInformation("AggregatedPool index template created: {ok}", response.IsValidResponse);
        }

        public Task InitializeFromExistingPoolsAsync(IEnumerable<Model.Data.Pool> pools, CancellationToken cancellationToken = default)
        {
            try
            {
                var aggregates = AggregatedPool.FromPools(pools).ToList();
                foreach (var agg in aggregates)
                {
                    _cache[(agg.AssetIdA, agg.AssetIdB)] = agg;
                }

                // Store and publish in background
                _ = Task.Run(async () =>
                {
                    foreach (var agg in aggregates)
                    {
                        await StoreAndPublishAsync(agg, cancellationToken);
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize AggregatedPool cache");
            }

            return Task.CompletedTask;
        }

        public async Task UpdateForPairAsync(ulong assetIdA, ulong assetIdB, IEnumerable<Model.Data.Pool> poolsForPair, CancellationToken cancellationToken = default)
        {
            try
            {
                // Recompute aggregate for this pair only
                var agg = AggregatedPool.FromPools(poolsForPair).FirstOrDefault();
                if (agg == null)
                {
                    // No pools remain for this pair; clear cache and send empty (or skip). We'll skip for now.
                    _cache.TryRemove((assetIdA, assetIdB), out _);
                    _logger.LogDebug("No pools for pair {a}-{b}; removed from cache", assetIdA, assetIdB);
                    return;
                }

                _cache[(agg.AssetIdA, agg.AssetIdB)] = agg;
                await StoreAndPublishAsync(agg, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update aggregated pool for pair {a}-{b}", assetIdA, assetIdB);
            }
        }

        private async Task StoreAndPublishAsync(AggregatedPool agg, CancellationToken cancellationToken)
        {
            try
            {
                // Store to Elasticsearch
                var id = $"{agg.AssetIdA}_{agg.AssetIdB}";
                var response = await _elasticClient.IndexAsync(agg, idx => idx
                    .Index("aggregatedpools")
                    .Id(id), cancellationToken);

                if (!response.IsValidResponse)
                {
                    _logger.LogWarning("Failed to index aggregated pool {id}: {error}", id, response.DebugInformation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store aggregated pool {a}-{b}", agg.AssetIdA, agg.AssetIdB);
            }

            try
            {
                // Publish to hub (simple broadcast like other repos)
                var send = agg;
                if (agg.AssetIdA > agg.AssetIdB)
                {
                    // Ensure consistent order for the pair
                    send = agg.Reverse();
                }

                BiatecScanHub.RecentAggregatedPoolUpdates.Enqueue(send);
                if (BiatecScanHub.RecentAggregatedPoolUpdates.Count > 100)
                {
                    BiatecScanHub.RecentAggregatedPoolUpdates.TryDequeue(out _);
                }
                if (send.AssetIdA == 0 && send.AssetIdB == 31566704)
                {
                    BiatecScanHub.ALGOUSD = send;
                }

                await _hubContext.Clients.All.SendAsync("AggregatedPoolUpdated", send, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish AggregatedPoolUpdated for {a}-{b}", agg.AssetIdA, agg.AssetIdB);
            }
        }
    }
}
