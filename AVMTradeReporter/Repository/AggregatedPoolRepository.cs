using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.Nodes;
using Elastic.Clients.Elasticsearch.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using AVMTradeReporter.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AVMTradeReporter.Repository
{
    public class AggregatedPoolRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<AggregatedPoolRepository> _logger;
        private readonly IHubContext<BiatecScanHub> _hubContext;
        private readonly IAssetRepository? _assetRepository; // optional asset repository for price/tvl updates
        private readonly IDatabase? _redisDatabase;
        private readonly AppConfiguration _appConfig;
        private readonly ISubscriber? _redisSubscriber; // cached Redis subscriber
        private readonly IServiceProvider _serviceProvider;

        private static readonly ConcurrentDictionary<(ulong A, ulong B), AggregatedPool> _cache = new();

        public AggregatedPoolRepository(
            ElasticsearchClient elasticClient,
            ILogger<AggregatedPoolRepository> logger,
            IHubContext<BiatecScanHub> hubContext,
            IOptions<AppConfiguration> appConfig,
            IServiceProvider serviceProvider,
            IDatabase? redisDatabase = null,
            IAssetRepository? assetRepository = null
)
        {
            _elasticClient = elasticClient;
            _logger = logger;
            _hubContext = hubContext;
            _assetRepository = assetRepository;
            _redisDatabase = redisDatabase;
            _appConfig = appConfig.Value;
            _redisSubscriber = _redisDatabase?.Multiplexer.GetSubscriber();
            _serviceProvider = serviceProvider;

            CreateIndexTemplateAsync().Wait();
        }

        private async Task CreateIndexTemplateAsync()
        {
            var templateRequest = new PutIndexTemplateRequest
            {
                Name = "aggregatedpools_template",
                IndexPatterns = new[] { "aggregatedpools-*" },
                // Not a data stream: aggregated pools are upserted by id (mutable latest-state docs),
                // which data streams reject - same failure mode TradeRepository hit on the fresh stage cluster.
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

        public async Task InitializeFromExistingPoolsAsync(IEnumerable<Models.Data.Pool> pools, CancellationToken cancellationToken = default)
        {
            try
            {
                var aggregates = AggregatedPool.FromPools(pools).ToList();
                foreach (var agg in aggregates)
                {
                    _cache[(agg.AssetIdA, agg.AssetIdB)] = agg;
                }

                // Store, publish and (critically) recompute derived asset PriceUSD/TVL/PoolsCount/
                // Volume figures - this used to run as a fire-and-forget Task.Run, which let
                // Program.cs's startup Wait() (and therefore Kestrel's port bind / the readiness
                // probe) return before the Assets page's numbers were actually warm. That let a
                // freshly-rolled pod pass its readiness check and receive production traffic while
                // still serving zero/stale asset stats, breaking the "new pod looks identical to the
                // old one" requirement for unnoticed HA deploys. Must be awaited here so the caller
                // (PoolRepository.InitializeAsync, blocking-awaited from Program.cs before the app
                // starts listening) cannot return until every asset stat is fully caught up.
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize AggregatedPool cache");
            }
        }

        public async Task UpdateForPairAsync(ulong assetIdA, ulong assetIdB, IEnumerable<Models.Data.Pool> poolsForPair, CancellationToken cancellationToken = default)
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
        /// <param name="orderBy">Optional server-side ordering. When specified, the result set is sorted before
        /// <paramref name="offset"/>/<paramref name="size"/> are applied so that pagination returns the top items.
        /// Pass <see langword="null"/> to keep the (undefined) cache enumeration order.</param>
        /// <param name="direction">Sort direction applied when <paramref name="orderBy"/> is specified. Defaults to descending.</param>
        /// <returns>A collection of <see cref="AggregatedPool"/> objects that match the specified filters and pagination
        /// parameters. If no filters are applied, all available pools are returned within the specified range.</returns>
        public IEnumerable<AggregatedPool> GetAllAggregatedPools(ulong? assetIdA, ulong? assetIdB, int offset = 0, int size = 100, Models.Data.Enums.PoolOrderBy? orderBy = null, Models.Data.Enums.SortDirection direction = Models.Data.Enums.SortDirection.Desc)
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
            if (orderBy.HasValue)
            {
                filteredPools = ApplyOrdering(filteredPools, orderBy.Value, direction);
            }
            return filteredPools.Skip(offset).Take(size);
        }

        private static IEnumerable<AggregatedPool> ApplyOrdering(IEnumerable<AggregatedPool> pools, Models.Data.Enums.PoolOrderBy orderBy, Models.Data.Enums.SortDirection direction)
        {
            Func<AggregatedPool, decimal> key = orderBy switch
            {
                Models.Data.Enums.PoolOrderBy.TVL => p => (p.TotalTVLAssetAInUSD ?? 0) + (p.TotalTVLAssetBInUSD ?? 0),
                Models.Data.Enums.PoolOrderBy.Volume1H => p => p.Volume1H ?? 0,
                Models.Data.Enums.PoolOrderBy.Volume24H => p => p.Volume24H ?? 0,
                Models.Data.Enums.PoolOrderBy.Volume7D => p => p.Volume7D ?? 0,
                Models.Data.Enums.PoolOrderBy.PoolCount => p => p.PoolCount,
                Models.Data.Enums.PoolOrderBy.LastUpdated => p => (p.LastUpdated ?? DateTimeOffset.MinValue).UtcTicks,
                _ => p => 0m
            };
            var ordered = direction == Models.Data.Enums.SortDirection.Asc
                ? pools.OrderBy(key)
                : pools.OrderByDescending(key);
            // Deterministic tie-break so that offset-based pagination is stable.
            return ordered.ThenBy(p => p.AssetIdA).ThenBy(p => p.AssetIdB);
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
        /// <summary>
        /// Picks an asset's USD price from the deeper of its two price routes: the direct
        /// asset↔<paramref name="usdRef"/> pair, or the asset↔ALGO pair scaled by ALGO's USD
        /// price. Depth is the real USD value of the anchor-side liquidity (the usdRef side of
        /// the direct pair; the ALGO side × <paramref name="algoPriceUsd"/> for the ALGO route),
        /// so a near-empty direct USDC pool can never override a liquid ALGO market — the
        /// unconditional direct-pair preference this replaces let a $0.0001 USDC pool misprice
        /// an asset by 214x. Returns null when neither route can produce a price.
        /// </summary>
        internal static decimal? SelectAssetUsdPrice(ulong assetId, ulong usdRef, AggregatedPool? directUsdPair, AggregatedPool? algoPair, decimal? algoPriceUsd)
        {
            decimal? directPrice = null;
            decimal directDepthUsd = 0m;
            if (directUsdPair != null)
            {
                var orient = directUsdPair.AssetIdB == usdRef ? directUsdPair : directUsdPair.Reverse();
                if (orient.AssetIdA == assetId && orient.VirtualSumALevel1ForPrice > 0 && orient.VirtualSumBLevel1ForPrice > 0)
                {
                    directPrice = orient.VirtualSumBLevel1ForPrice.Value / orient.VirtualSumALevel1ForPrice.Value; // usdRef per asset
                    // TVL_B_ForPrice (not TVL_B): depth must only count pools that actually voted on the
                    // price above, or a wall/depleted out-of-range pool's real balance could make this route
                    // look deeper than it tradeably is without ever influencing directPrice itself.
                    directDepthUsd = orient.TVL_B_ForPrice; // real usdRef units ≈ USD
                }
            }

            decimal? algoRoutePrice = null;
            decimal algoDepthUsd = 0m;
            if (algoPair != null && algoPriceUsd > 0)
            {
                var orient = algoPair.AssetIdA == assetId ? algoPair : algoPair.Reverse();
                if (orient.AssetIdB == 0UL && orient.VirtualSumALevel1ForPrice > 0 && orient.VirtualSumBLevel1ForPrice > 0)
                {
                    var algoPerAsset = orient.VirtualSumBLevel1ForPrice.Value / orient.VirtualSumALevel1ForPrice.Value;
                    algoRoutePrice = algoPerAsset * algoPriceUsd.Value; // USD per asset
                    algoDepthUsd = orient.TVL_B_ForPrice * algoPriceUsd.Value; // real ALGO units (price-voting pools only) × ALGO price
                }
            }

            if (directPrice.HasValue && algoRoutePrice.HasValue)
            {
                return directDepthUsd >= algoDepthUsd ? directPrice : algoRoutePrice;
            }
            return directPrice ?? algoRoutePrice;
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

                // Persist to Redis for subscriber preload
                if (_redisDatabase != null && _appConfig.Redis.Enabled)
                {
                    try
                    {
                        var redisKey = $"{_appConfig.Redis.AggregatedPoolKeyPrefix}{agg.AssetIdA}-{agg.AssetIdB}";
                        var indexKey = $"{_appConfig.Redis.AggregatedPoolKeyPrefix}index";
                        var aggregatedPoolJson = JsonSerializer.Serialize(agg);
                        await _redisDatabase.StringSetAsync(redisKey, aggregatedPoolJson);
                        await _redisDatabase.SetAddAsync(indexKey, $"{agg.AssetIdA}-{agg.AssetIdB}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to persist aggregated pool to Redis {a}-{b}", agg.AssetIdA, agg.AssetIdB);
                    }
                }

                // Publish to Redis PubSub channel
                if (_redisSubscriber != null && _appConfig.Redis.Enabled)
                {
                    try
                    {
                        var aggregatedPoolJson = JsonSerializer.Serialize(agg);
                        await _redisSubscriber.PublishAsync(RedisChannel.Literal(_appConfig.Redis.AggregatedPoolUpdateChannel), aggregatedPoolJson);
                        //_logger.LogDebug("Published aggregated pool update to Redis PubSub channel: {channel}", _appConfig.Redis.AggregatedPoolUpdateChannel);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to publish aggregated pool to Redis PubSub: {a}-{b}", agg.AssetIdA, agg.AssetIdB);
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
                if (send.AssetIdA == 0 && send.AssetIdB == _appConfig.UsdReferenceAssetId)
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
                var usdRef = _appConfig.UsdReferenceAssetId;

                // Assets potentially affected: both sides plus reference assets (ALGO=0, usdRef)
                var affected = new HashSet<ulong> { updatedPool.AssetIdA, updatedPool.AssetIdB, 0UL, usdRef };
                // Ensure ALGO/usdRef prices first so that derived prices can use them
                var ordered = affected.OrderBy(a => a == 0 ? 0 : a == usdRef ? 1 : 2).ToArray();

                // Cache for quick price lookup
                var priceCache = new Dictionary<ulong, decimal>();

                foreach (var assetId in ordered)
                {
                    var asset = await _assetRepository.GetAssetAsync(assetId, cancellationToken);
                    if (asset == null) continue;
                    var changed = false;

                    // Peg this network's reference stable asset (e.g. USDC) to $1
                    if (asset.Index == usdRef && asset.PriceUSD != 1m)
                    {
                        asset.PriceUSD = 1m;
                        changed = true;
                    }

                    // Calculate PriceUSD
                    decimal newPrice = asset.PriceUSD;
                    if (assetId == usdRef)
                    {
                        newPrice = 1m; // reference stable asset assumed $1
                    }
                    else if (assetId == 0UL)
                    {
                        // ALGO price from ALGO/usdRef pair (orientation A=0, B=usdRef if possible)
                        var algoUsdc = GetAggregatedPool(0, usdRef);
                        if (algoUsdc != null)
                        {
                            var orient = algoUsdc.AssetIdB == usdRef ? algoUsdc : algoUsdc.Reverse();
                            if (orient.VirtualSumALevel1ForPrice > 0)
                            {
                                newPrice = (orient.VirtualSumBLevel1ForPrice ?? 0) / (orient.VirtualSumALevel1ForPrice ?? 0); // usdRef per ALGO
                            }
                        }
                    }
                    else
                    {
                        // Price from the deeper of the two routes (direct usdRef pair vs via
                        // ALGO) — never from a fixed route preference. A dust direct-USDC pool
                        // must not override a liquid ALGO market (a $0.0001 USDC pool once priced
                        // MEEP 214x above its real ALGO-pair market, faking +99% "gains").
                        var pairUsdc = GetAggregatedPool(assetId, usdRef);
                        var pairAlgo = GetAggregatedPool(assetId, 0);
                        var algoAsset = await _assetRepository.GetAssetAsync(0, cancellationToken);
                        var selected = SelectAssetUsdPrice(assetId, usdRef, pairUsdc, pairAlgo, algoAsset?.PriceUSD);
                        if (selected > 0)
                        {
                            newPrice = selected.Value;
                        }
                    }

                    if (newPrice > 0 && newPrice != asset.PriceUSD)
                    {
                        asset.PriceUSD = newPrice;
                        changed = true;
                    }
                    priceCache[assetId] = asset.PriceUSD;

                    // Set historical prices
                    var ohlcService = _serviceProvider?.GetService<IOHLCService>();
                    
                    if (ohlcService != null)
                    {
                        asset.PriceUSD1H = await ohlcService.GetHistoricalPriceAsync(assetId, TimeSpan.FromHours(1), cancellationToken);
                        asset.PriceUSD24H = await ohlcService.GetHistoricalPriceAsync(assetId, TimeSpan.FromHours(24), cancellationToken);
                        asset.PriceUSD7D = await ohlcService.GetHistoricalPriceAsync(assetId, TimeSpan.FromDays(7), cancellationToken);
                    }

                    // Calculate Real TVL (TVL_USD) and Total TVL (TotalTVLAssetInUSD)
                    // Real TVL: Only trusted tokens from pools paired with trusted references
                    // Total TVL: All assets (both sides) from pools paired with trusted references
                    // Trusted reference tokens: ALGO=0, this network's usdRef, and configured
                    // other stablecoins/major tokens (AppConfiguration.TrustedReferenceAssetIds).
                    HashSet<ulong> refs = new HashSet<ulong>(_appConfig.TrustedReferenceAssetIds) { 0UL, usdRef }; // duplicates automatically removed by HashSet
                    decimal realTvlUsd = 0m;    // Real TVL: sum of trusted token values only
                    decimal totalTvlUsd = 0m;   // Total TVL: sum of all asset values

                    // Sum USD value of all aggregated pools where the other asset is trusted reference
                    var processedPairs = new HashSet<string>();
                    foreach (var ap in _cache.Values.Where(p => (p.AssetIdA == assetId && refs.Contains(p.AssetIdB)) || (p.AssetIdB == assetId && refs.Contains(p.AssetIdA))))
                    {
                        var key = ap.AssetIdA < ap.AssetIdB ? $"{ap.AssetIdA}-{ap.AssetIdB}" : $"{ap.AssetIdB}-{ap.AssetIdA}";
                        if (!processedPairs.Add(key)) continue; // skip already counted (since _cache stores both directions)

                        // Determine orientation
                        ulong otherAssetId = ap.AssetIdA == assetId ? ap.AssetIdB : ap.AssetIdA;

                        // Ensure we have prices for both sides
                        if (!priceCache.TryGetValue(otherAssetId, out var otherPrice))
                        {
                            var otherAsset = await _assetRepository.GetAssetAsync(otherAssetId, cancellationToken);
                            if (otherAsset != null && otherAsset.PriceUSD > 0)
                            {
                                otherPrice = otherAsset.PriceUSD;
                                priceCache[otherAssetId] = otherPrice;
                            }
                        }
                        // Refresh priceAsset if not present (may have been updated earlier in loop)
                        priceCache.TryGetValue(assetId, out var priceAssetCurrent);
                        if (priceAssetCurrent <= 0) priceAssetCurrent = asset.PriceUSD;

                        // NOTE: PriceUSD is intentionally NOT re-derived from the usdRef pair
                        // here — route selection above (SelectAssetUsdPrice) already chose the
                        // deeper market, and re-deriving from any direct usdRef pair would let a
                        // dust USDC pool override that choice again.

                        if (priceAssetCurrent <= 0 || otherPrice <= 0) continue; // skip TVL calculation until both prices known

                        // Calculate Real TVL: only the trusted token side (otherAssetId is the trusted reference)
                        decimal trustedTokenValue;
                        if (ap.AssetIdA == assetId)
                        {
                            // Asset is on side A, trusted token is on side B
                            trustedTokenValue = ap.TVL_B * otherPrice;
                        }
                        else
                        {
                            // Asset is on side B, trusted token is on side A
                            trustedTokenValue = ap.TVL_A * otherPrice;
                        }
                        if (trustedTokenValue > 0) realTvlUsd += trustedTokenValue;

                        // Calculate Total TVL: both sides of the pool
                        decimal poolTotalUsd;
                        if (ap.AssetIdA == assetId)
                        {
                            poolTotalUsd = ap.TVL_A * priceAssetCurrent + ap.TVL_B * otherPrice;
                        }
                        else
                        {
                            poolTotalUsd = ap.TVL_B * priceAssetCurrent + ap.TVL_A * otherPrice;
                        }
                        if (poolTotalUsd > 0) totalTvlUsd += poolTotalUsd;
                    }

                    // Set Real TVL (TVL_USD) - only trusted tokens
                    if (realTvlUsd > 0 && realTvlUsd != asset.TVL_USD)
                    {
                        asset.TVL_USD = realTvlUsd;
                        changed = true;
                    }

                    // Set Total TVL (TotalTVLAssetInUSD) - all assets
                    if (totalTvlUsd > 0 && asset.TotalTVLAssetInUSD != totalTvlUsd)
                    {
                        asset.TotalTVLAssetInUSD = totalTvlUsd;
                        changed = true;
                    }

                    // Calculate number of distinct pools (asset pairs) involving this asset.
                    // _cache stores each pair keyed both (A,B) and (B,A), so dedupe by pair.
                    int poolsCount = _cache.Values
                        .Where(p => p.AssetIdA == assetId || p.AssetIdB == assetId)
                        .Select(p => p.AssetIdA < p.AssetIdB ? $"{p.AssetIdA}-{p.AssetIdB}" : $"{p.AssetIdB}-{p.AssetIdA}")
                        .Distinct()
                        .Count();
                    if (poolsCount != asset.PoolsCount)
                    {
                        asset.PoolsCount = poolsCount;
                        changed = true;
                    }

                    // Calculate volumes from all aggregated pools involving this asset
                    decimal volume1H = _cache.Values
                        .Where(p => p.AssetIdA == assetId || p.AssetIdB == assetId)
                        .Sum(p => p.Volume1H ?? 0) / 2;
                    decimal volume24H = _cache.Values
                        .Where(p => p.AssetIdA == assetId || p.AssetIdB == assetId)
                        .Sum(p => p.Volume24H ?? 0) / 2;
                    decimal volume7D = _cache.Values
                        .Where(p => p.AssetIdA == assetId || p.AssetIdB == assetId)
                        .Sum(p => p.Volume7D ?? 0) / 2;

                    // Set volumes
                    if (volume1H != asset.Volume1H)
                    {
                        asset.Volume1H = volume1H;
                        changed = true;
                    }
                    if (volume24H != asset.Volume24H)
                    {
                        asset.Volume24H = volume24H;
                        changed = true;
                    }
                    if (volume7D != asset.Volume7D)
                    {
                        asset.Volume7D = volume7D;
                        changed = true;
                    }

                    if (changed)
                    {
                        asset.Timestamp = updatedPool.LastUpdated > asset.Timestamp ? updatedPool.LastUpdated : asset.Timestamp;
                        await _assetRepository.SetAssetAsync(asset, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update asset prices/TVL after aggregated pool update {a}-{b}", updatedPool.AssetIdA, updatedPool.AssetIdB);
            }

            // Update historical prices on aggregated pools
            foreach (var agg in _cache.Values)
            {
                var assetA = await _assetRepository?.GetAssetAsync(agg.AssetIdA, cancellationToken);
                var assetB = await _assetRepository?.GetAssetAsync(agg.AssetIdB, cancellationToken);
                agg.PriceAUSD1H = assetA?.PriceUSD1H;
                agg.PriceAUSD24H = assetA?.PriceUSD24H;
                agg.PriceAUSD7D = assetA?.PriceUSD7D;
                agg.PriceBUSD1H = assetB?.PriceUSD1H;
                agg.PriceBUSD24H = assetB?.PriceUSD24H;
                agg.PriceBUSD7D = assetB?.PriceUSD7D;
            }
        }
    }
}
