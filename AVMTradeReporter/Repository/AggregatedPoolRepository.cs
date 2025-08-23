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
        private readonly IAssetRepository? _assetRepository; // optional asset repository for price/tvl updates

        private static readonly ConcurrentDictionary<(ulong A, ulong B), AggregatedPool> _cache = new();

        public AggregatedPoolRepository(
            ElasticsearchClient elasticClient,
            ILogger<AggregatedPoolRepository> logger,
            IHubContext<BiatecScanHub> hubContext,
            IAssetRepository? assetRepository = null)
        {
            _elasticClient = elasticClient;
            _logger = logger;
            _hubContext = hubContext;
            _assetRepository = assetRepository;

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
            try
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish AggregatedPoolUpdated for {a}-{b}", send.AssetIdA, send.AssetIdB);
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

                // Update related asset prices / tvl
                await UpdateRelatedAssetsAsync(agg, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish AggregatedPoolUpdated for {a}-{b}", agg.AssetIdA, agg.AssetIdB);
            }
        }

        private async Task UpdateRelatedAssetsAsync(AggregatedPool updatedPool, CancellationToken cancellationToken)
        {
            if (_assetRepository == null) return; // Feature disabled if repository not supplied
            try
            {
                // Assets potentially affected: both sides plus reference assets (ALGO=0, USDC=31566704)
                var affected = new HashSet<ulong> { updatedPool.AssetIdA, updatedPool.AssetIdB, 0UL, 31566704UL };
                // Ensure ALGO/USDC prices first so that derived prices can use them
                var ordered = affected.OrderBy(a => a == 0 ? 0 : a == 31566704 ? 1 : 2).ToArray();

                // Cache for quick price lookup
                var priceCache = new Dictionary<ulong, decimal>();

                foreach (var assetId in ordered)
                {
                    var asset = await _assetRepository.GetAssetAsync(assetId, cancellationToken);
                    if (asset == null) continue;
                    bool changed = false;

                    // Calculate PriceUSD
                    decimal newPrice = asset.PriceUSD;
                    if (assetId == 31566704UL)
                    {
                        newPrice = 1m; // USDC assumed $1
                    }
                    else if (assetId == 0UL)
                    {
                        // ALGO price from ALGO/USDC pair (orientation A=0, B=USDC if possible)
                        var algoUsdc = GetAggregatedPool(0, 31566704);
                        if (algoUsdc != null)
                        {
                            var orient = algoUsdc.AssetIdB == 31566704 ? algoUsdc : algoUsdc.Reverse();
                            if (orient.VirtualSumALevel1 > 0)
                            {
                                newPrice = orient.VirtualSumBLevel1 / orient.VirtualSumALevel1; // USDC per ALGO
                            }
                        }
                    }
                    else
                    {
                        // 1. Try direct asset-USDC pair
                        var pairUsdc = GetAggregatedPool(assetId, 31566704);
                        if (pairUsdc != null)
                        {
                            var orient = pairUsdc.AssetIdB == 31566704 ? pairUsdc : pairUsdc.Reverse();
                            if (orient.VirtualSumALevel1 > 0)
                            {
                                newPrice = orient.VirtualSumBLevel1 / orient.VirtualSumALevel1; // USDC per asset
                            }
                        }
                        else
                        {
                            // 2. Derive via ALGO if available (asset-ALGO)
                            var algoAsset = await _assetRepository.GetAssetAsync(0, cancellationToken);
                            var pairAlgo = GetAggregatedPool(assetId, 0);
                            if (algoAsset?.PriceUSD > 0 && pairAlgo != null)
                            {
                                var orient = pairAlgo.AssetIdA == assetId ? pairAlgo : pairAlgo.Reverse();
                                if (orient.VirtualSumALevel1 > 0)
                                {
                                    var algoPerAsset = orient.VirtualSumBLevel1 / orient.VirtualSumALevel1; // ALGO per asset
                                    newPrice = algoPerAsset * algoAsset.PriceUSD; // USD per asset
                                }
                            }
                        }
                    }

                    if (newPrice > 0 && newPrice != asset.PriceUSD)
                    {
                        asset.PriceUSD = newPrice;
                        changed = true;
                    }
                    priceCache[assetId] = asset.PriceUSD;

                    // Calculate TVL_USD using trusted reference pairs (ALGO=0, USDC=31566704)
                    ulong[] refs = new[] {
                        0UL, 31566704UL, 1134696561UL, 2537013734UL, 1185173782UL,
                        386192725UL,1058926737UL,2400334372UL,760037151UL,386195940UL, 386195940UL,
                        246516580UL, 246519683UL,227855942UL, 2320775407UL, 887406851UL,887648583UL,
                        1241945177UL, 1241944285UL, 2320804780UL
                    };
                    decimal tvlUsd = 0m;
                    foreach (var refAsset in refs)
                    {
                        if (refAsset == assetId) continue; // skip self
                        var pair = GetAggregatedPool(assetId, refAsset);
                        if (pair == null) continue;
                        var orient = pair.AssetIdA == assetId ? pair : pair.Reverse();
                        // Prices
                        decimal priceAsset = priceCache.TryGetValue(assetId, out var pA) ? pA : (asset.PriceUSD > 0 ? asset.PriceUSD : 0);
                        decimal priceRef = 0m;
                        if (refAsset == 31566704UL) priceRef = 1m; // USDC
                        else if (refAsset == 0UL)
                        {
                            if (!priceCache.TryGetValue(0UL, out priceRef))
                            {
                                var algoAsset = await _assetRepository.GetAssetAsync(0, cancellationToken);
                                priceRef = algoAsset?.PriceUSD ?? 0m;
                                priceCache[0UL] = priceRef;
                            }
                        }
                        if (priceAsset <= 0 || priceRef <= 0) continue; // need both prices
                        // TVL contribution = USD value of both sides
                        var poolUsd = orient.TVL_A * priceAsset + orient.TVL_B * priceRef;
                        if (poolUsd > 0) tvlUsd += poolUsd;
                    }
                    if (tvlUsd > 0 && tvlUsd != asset.TVL_USD)
                    {
                        asset.TVL_USD = tvlUsd;
                        changed = true;
                    }

                    if (changed)
                    {
                        await _assetRepository.SetAssetAsync(asset, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update asset prices/TVL after aggregated pool update {a}-{b}", updatedPool.AssetIdA, updatedPool.AssetIdB);
            }
        }
    }
}
