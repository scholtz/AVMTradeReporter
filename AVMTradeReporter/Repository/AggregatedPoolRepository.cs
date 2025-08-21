using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.Nodes;
using Elastic.Clients.Elasticsearch.Security;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace AVMTradeReporter.Repository
{
    public class AggregatedPoolRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<AggregatedPoolRepository> _logger;
        private readonly IHubContext<BiatecScanHub> _hubContext;

        private static readonly ConcurrentDictionary<(ulong A, ulong B), AggregatedPool> _cache = new();

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
            if (_elasticClient == null)
            {
                _logger.LogError("Elasticsearch client is not initialized");
                return;
            }
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
                        var send = agg;
                        if (agg.AssetIdA > agg.AssetIdB)
                        {
                            // Ensure consistent order for the pair
                            send = agg.Reverse();
                        }
                        await StoreAggregatedPoolAsync(send, cancellationToken);
                        await PublishToHubAsync(send, cancellationToken);
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
                var agg = AggregatedPool.FromPools(poolsForPair).FirstOrDefault(p => p.AssetIdA == assetIdA && p.AssetIdB == assetIdB);
                if (agg == null)
                {
                    // No pools remain for this pair; clear cache and send empty (or skip). We'll skip for now.
                    _cache.TryRemove((assetIdA, assetIdB), out _);
                    _logger.LogDebug("No pools for pair {a}-{b}; removed from cache", assetIdA, assetIdB);
                    return;
                }

                var send = agg;
                if (agg.AssetIdA > agg.AssetIdB)
                {
                    // Ensure consistent order for the pair
                    send = agg.Reverse();
                }
                await StoreAggregatedPoolAsync(send, cancellationToken);
                await PublishToHubAsync(send, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update aggregated pool for pair {a}-{b}", assetIdA, assetIdB);
            }
        }
        /// <summary>
        /// Retrieves a collection of aggregated pools, optionally filtered by asset IDs, with support for pagination.
        /// </summary>
        /// <remarks>If both <paramref name="assetIdA"/> and <paramref name="assetIdB"/> are specified,
        /// the method returns pools that contain both assets, regardless of their order. If only one of the asset IDs
        /// is specified, the method returns pools that contain the specified asset. If neither is specified, all pools
        /// are returned.</remarks>
        /// <param name="assetIdA">The first asset ID to filter by. If specified, only pools containing this asset will be included. Pass <see
        /// langword="null"/> to ignore this filter.</param>
        /// <param name="assetIdB">The second asset ID to filter by. If specified, only pools containing this asset will be included. Pass <see
        /// langword="null"/> to ignore this filter.</param>
        /// <param name="offset">The number of items to skip before starting to return results. Must be non-negative.</param>
        /// <param name="size">The maximum number of items to return. Must be greater than zero.</param>
        /// <returns>A collection of <see cref="AggregatedPool"/> objects that match the specified filters and pagination
        /// parameters. If no filters are applied, all available pools are returned within the specified range.</returns>
        public IEnumerable<AggregatedPool> GetAllAggregatedPools(ulong? assetIdA, ulong? assetIdB, int offset = 0, int size = 100)
        {
            var filteredPools = _cache.Values.AsEnumerable();
            if (assetIdA.HasValue && assetIdB.HasValue)
            {
                filteredPools = filteredPools.Where(p => (p.AssetIdA == assetIdA.Value && p.AssetIdB == assetIdB.Value) ||
                                                         (p.AssetIdB == assetIdA.Value && p.AssetIdA == assetIdB.Value));
            }
            else if (assetIdA.HasValue)
            {
                filteredPools = filteredPools.Where(p => p.AssetIdA == assetIdA.Value || p.AssetIdB == assetIdA.Value);
            }
            else if (assetIdB.HasValue)
            {
                filteredPools = filteredPools.Where(p => p.AssetIdA == assetIdB.Value || p.AssetIdB == assetIdB.Value);
            }
            return filteredPools.Skip(offset).Take(size);
        }
        /// <summary>
        /// Retrieves the aggregated pool associated with the specified asset pair.
        /// </summary>
        /// <remarks>The method checks for the aggregated pool in both possible orderings of the asset
        /// pair  (i.e., <paramref name="assetIdA"/> followed by <paramref name="assetIdB"/>, and vice versa).</remarks>
        /// <param name="assetIdA">The ID of the first asset in the pair.</param>
        /// <param name="assetIdB">The ID of the second asset in the pair.</param>
        /// <returns>An <see cref="AggregatedPool"/> object representing the aggregated pool for the specified asset pair,  or
        /// <see langword="null"/> if no matching pool is found.</returns>
        public AggregatedPool? GetAggregatedPool(ulong assetIdA, ulong assetIdB)
        {
            if (_cache.TryGetValue((assetIdA, assetIdB), out var pool))
            {
                return pool;
            }
            if (_cache.TryGetValue((assetIdB, assetIdA), out pool))
            {
                return pool;
            }
            return null; // Not found
        }
        public async Task PublishToHubAsync(AggregatedPool send, CancellationToken cancellationToken = default)
        {
            if (send == null) throw new ArgumentNullException(nameof(send));
            // Ensure the pool is stored before publishing
            if (_hubContext == null)
            {
                _logger.LogWarning("Hub context is not initialized");
            }
            else
            {
                var subscriptions = BiatecScanHub.GetSubscriptions();

                var subscribedClientsConnections = new HashSet<string>();

                foreach (var subscription in subscriptions)
                {
                    var userId = subscription.Key;
                    var filter = subscription.Value;

                    if (BiatecScanHub.ShouldSendAggregatedPoolToUser(send, filter))
                    {
                        subscribedClientsConnections.Add(userId);
                    }
                }
                await _hubContext.Clients.Users(subscribedClientsConnections).SendAsync(BiatecScanHub.Subscriptions.AGGREGATED_POOL, send, cancellationToken);
            }
        }
        private async Task StoreAggregatedPoolAsync(AggregatedPool agg, CancellationToken cancellationToken)
        {
            _cache[(agg.AssetIdA, agg.AssetIdB)] = agg;
            _cache[(agg.AssetIdB, agg.AssetIdA)] = agg;
            try
            {
                if (_elasticClient != null)
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store aggregated pool {a}-{b}", agg.AssetIdA, agg.AssetIdB);
            }

            try
            {
                // Publish to hub (simple broadcast like other repos)
                var send = agg;
                BiatecScanHub.RecentAggregatedPoolUpdates.Enqueue(send);
                if (BiatecScanHub.RecentAggregatedPoolUpdates.Count > 100)
                {
                    BiatecScanHub.RecentAggregatedPoolUpdates.TryDequeue(out _);
                }
                if (send.AssetIdA == 0 && send.AssetIdB == 31566704)
                {
                    BiatecScanHub.ALGOUSD = send;
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish AggregatedPoolUpdated for {a}-{b}", agg.AssetIdA, agg.AssetIdB);
            }
        }
    }
}
