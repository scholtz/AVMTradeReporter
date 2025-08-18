using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Utilities;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace AVMTradeReporter.Repository
{
    public class PoolRepository : IPoolRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<PoolRepository> _logger;
        private readonly IHubContext<BiatecScanHub> _hubContext;
        private readonly AggregatedPoolRepository _aggregatedPoolRepository;
        private readonly IDatabase? _redisDatabase;
        private readonly AppConfiguration _appConfig;
        private readonly IServiceProvider _serviceProvider;

        // In-memory cache for pools
        private static readonly ConcurrentDictionary<string, Pool> _poolsCache = new();
        private readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
        private bool _isInitialized = false;

        public PoolRepository(
            ElasticsearchClient elasticClient,
            ILogger<PoolRepository> logger,
            IHubContext<BiatecScanHub> hubContext,
            AggregatedPoolRepository aggregatedPoolRepository,
            IOptions<AppConfiguration> appConfig,
            IServiceProvider serviceProvider,
            IDatabase? redisDatabase = null
            )
        {
            _elasticClient = elasticClient;
            _logger = logger;
            _hubContext = hubContext;
            _aggregatedPoolRepository = aggregatedPoolRepository;
            _redisDatabase = redisDatabase;
            _appConfig = appConfig.Value;
            _serviceProvider = serviceProvider;

            CreatePoolIndexTemplateAsync().Wait();
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _initializationSemaphore.WaitAsync(cancellationToken);
            try
            {
                if (_isInitialized)
                    return;

                _logger.LogInformation("Initializing PoolRepository - loading pools from Redis and Elasticsearch");

                // First try to load from Redis
                var redisLoadCount = await LoadPoolsFromRedis(cancellationToken);
                _logger.LogInformation("Loaded {count} pools from Redis", redisLoadCount);

                // If Redis is empty or disabled, load from Elasticsearch
                if (redisLoadCount == 0)
                {
                    var elasticLoadCount = await LoadPoolsFromElasticsearch(cancellationToken);
                    _logger.LogInformation("Loaded {count} pools from Elasticsearch", elasticLoadCount);

                    // Save to Redis for future starts
                    if (elasticLoadCount > 0 && _redisDatabase != null && _appConfig.Redis.Enabled)
                    {
                        await SaveAllPoolsToRedis(cancellationToken);
                        _logger.LogInformation("Saved {count} pools to Redis", elasticLoadCount);
                    }
                }

                _isInitialized = true;
                _logger.LogInformation("PoolRepository initialization completed. Total pools in memory: {count}", _poolsCache.Count);

                // Initialize aggregated pools from currently loaded pools
                try
                {
                    await _aggregatedPoolRepository.InitializeFromExistingPoolsAsync(_poolsCache.Values, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to initialize AggregatedPoolRepository from existing pools");
                }
            }
            finally
            {
                _initializationSemaphore.Release();
            }
        }

        private async Task<int> LoadPoolsFromRedis(CancellationToken cancellationToken)
        {
            if (_redisDatabase == null || !_appConfig.Redis.Enabled)
                return 0;

            try
            {
                var pattern = $"{_appConfig.Redis.KeyPrefix}*";
                var server = _redisDatabase.Multiplexer.GetServer(_redisDatabase.Multiplexer.GetEndPoints().First());
                var keys = server.Keys(pattern: pattern);

                int loadedCount = 0;
                foreach (var key in keys)
                {
                    try
                    {
                        var poolJson = await _redisDatabase.StringGetAsync(key);
                        if (poolJson.HasValue)
                        {
                            var pool = JsonSerializer.Deserialize<Pool>(poolJson!);
                            if (pool != null)
                            {
                                var poolId = GeneratePoolId(pool.PoolAddress);
                                _poolsCache.TryAdd(poolId, pool);
                                loadedCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize pool from Redis key: {key}", key);
                    }
                }

                return loadedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load pools from Redis");
                return 0;
            }
        }

        private async Task<int> LoadPoolsFromElasticsearch(CancellationToken cancellationToken)
        {
            try
            {
                if (_elasticClient == null)
                {
                    _logger.LogError("Elasticsearch client is not initialized");
                    return 0;
                }
                var searchResponse = await _elasticClient.SearchAsync<Pool>(s => s
                    .Indices("pools")
                    .Size(10000), cancellationToken);

                if (searchResponse.IsValidResponse)
                {
                    int loadedCount = 0;
                    foreach (var pool in searchResponse.Documents)
                    {
                        var poolId = GeneratePoolId(pool.PoolAddress);
                        _poolsCache.TryAdd(poolId, pool);
                        loadedCount++;
                    }

                    return loadedCount;
                }

                _logger.LogError("Failed to load pools from Elasticsearch: {error}", searchResponse.DebugInformation);
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load pools from Elasticsearch");
                return 0;
            }
        }

        private async Task SaveAllPoolsToRedis(CancellationToken cancellationToken)
        {
            if (_redisDatabase == null || !_appConfig.Redis.Enabled)
                return;

            try
            {
                var tasks = _poolsCache.Values.Select(async pool =>
                {
                    var poolId = GeneratePoolId(pool.PoolAddress);
                    var redisKey = $"{_appConfig.Redis.KeyPrefix}{poolId}";
                    var poolJson = JsonSerializer.Serialize(pool);
                    await _redisDatabase.StringSetAsync(redisKey, poolJson);
                });

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save pools to Redis");
            }
        }

        async Task CreatePoolIndexTemplateAsync()
        {
            var templateRequest = new PutIndexTemplateRequest
            {
                Name = "pools_template",
                IndexPatterns = new[] { "pools-*" },
                DataStream = new DataStreamVisibility(),
                Template = new IndexTemplateMapping
                {
                    Mappings = new TypeMapping
                    {
                        Properties = new Properties
                        {
                            { "poolAddress", new KeywordProperty() },
                            { "poolAppId", new LongNumberProperty() },
                            { "assetIdA", new LongNumberProperty() },
                            { "assetIdB", new LongNumberProperty() },
                            { "assetIdLP", new LongNumberProperty() },
                            { "assetAmountA", new LongNumberProperty() },
                            { "assetAmountB", new LongNumberProperty() },
                            { "assetAmountLP", new LongNumberProperty() },
                            { "a", new LongNumberProperty() },
                            { "b", new LongNumberProperty() },
                            { "l", new LongNumberProperty() },
                            { "protocol", new KeywordProperty() },
                            { "timestamp", new DateProperty() }
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
            Console.WriteLine($"Pool template created: {response.IsValidResponse}");
        }

        public async Task<Pool?> GetPoolAsync(string poolAddress, CancellationToken cancellationToken)
        {
            await EnsureInitialized(cancellationToken);
            return _poolsCache.TryGetValue(poolAddress, out var pool) ? pool : null;
        }

        public async Task<bool> StorePoolAsync(Pool pool, CancellationToken cancellationToken)
        {
            await EnsureInitialized(cancellationToken);

            try
            {

                // Update in-memory cache
                _poolsCache[pool.PoolAddress] = pool;

                BiatecScanHub.RecentPoolUpdates.Enqueue(pool);
                if (BiatecScanHub.RecentPoolUpdates.Count > 100)
                {
                    BiatecScanHub.RecentPoolUpdates.TryDequeue(out _);
                }

                // Save to Redis asynchronously
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_redisDatabase != null && _appConfig.Redis.Enabled)
                        {
                            var redisKey = $"{_appConfig.Redis.KeyPrefix}{pool.PoolAddress}";
                            var poolJson = JsonSerializer.Serialize(pool);
                            await _redisDatabase.StringSetAsync(redisKey, poolJson);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to save pool to Redis: {poolId}", pool.PoolAddress);
                    }
                }, cancellationToken);

                // Save to Elasticsearch asynchronously
                if (_elasticClient != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var response = await _elasticClient.IndexAsync(pool, idx => idx
                                .Index("pools")
                                .Id(pool.PoolAddress), cancellationToken);

                            if (!response.IsValidResponse)
                            {
                                _logger.LogError("Failed to store pool in Elasticsearch {poolId}: {error}", pool.PoolAddress, response.DebugInformation);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to store pool in Elasticsearch: {poolId}", pool.PoolAddress);
                        }
                    }, cancellationToken);
                }

                _logger.LogDebug("Pool updated in memory: {poolId}", pool.PoolAddress);

                // Publish pool update to SignalR hub
                await PublishPoolUpdateToHub(pool, cancellationToken);

                // Update aggregated view for this asset pair if asset ids are present
                if (pool.AssetIdA.HasValue && pool.AssetIdB.HasValue)
                {
                    await UpdateAggregatedPool(pool.AssetIdA.Value, pool.AssetIdB.Value, cancellationToken);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store pool");
                return false;
            }
        }
        public async Task UpdateAggregatedPool(ulong aId, ulong bId, CancellationToken cancellationToken)
        {
            await EnsureInitialized(cancellationToken);
            try
            {
                // Update aggregated pool for this asset pair
                var poolsForPair = _poolsCache.Values.Where(p => (p.AssetIdA == aId && p.AssetIdB == bId) || (p.AssetIdA == bId && p.AssetIdB == aId));
                if (poolsForPair != null)
                {
                    await _aggregatedPoolRepository.UpdateForPairAsync(aId, bId, poolsForPair, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update aggregated pool for assets {assetIdA} and {assetIdB}", aId, bId);
            }
        }

        public async Task UpdatePoolFromTrade(Trade trade, CancellationToken cancellationToken)
        {
            // Only update from confirmed tradeError refreshing pools
            if (trade.TradeState != TxState.Confirmed)
            {
                _logger.LogDebug("Skipping pool update from unconfirmed trade {txId}", trade.TxId);
                return;
            }

            await EnsureInitialized(cancellationToken);

            try
            {
                var poolId = GeneratePoolId(trade.PoolAddress);
                var existingPool = _poolsCache.TryGetValue(poolId, out var pool) ? pool : null;

                // If pool doesn't exist, create a new one from trade data
                if (existingPool == null)
                {
                    var newPool = await CreatePoolFromTrade(trade);

                    // Check if we need to load full pool data using pool processor
                    if (string.IsNullOrEmpty(newPool.ApprovalProgramHash))
                    {
                        var enrichedPool = await TryEnrichPoolWithProcessor(newPool, cancellationToken);
                        if (enrichedPool != null)
                        {
                            newPool = enrichedPool;
                        }
                    }

                    await StorePoolAsync(newPool, cancellationToken);
                    _logger.LogInformation("Created new pool from trade: {poolAddress}_{poolAppId}_{protocol}",
                        trade.PoolAddress, trade.PoolAppId, trade.Protocol);
                    return;
                }

                // Only update if timestamp is equal or larger
                if (trade.Timestamp.HasValue && existingPool.Timestamp.HasValue &&
                    trade.Timestamp.Value < existingPool.Timestamp.Value)
                {
                    _logger.LogDebug("Skipping pool update - trade timestamp {tradeTime} is older than pool timestamp {poolTime}",
                        trade.Timestamp.Value, existingPool.Timestamp.Value);
                    return;
                }

                existingPool.A = trade.A;
                existingPool.B = trade.B;
                if (trade.L > 0)
                    existingPool.L = trade.L;
                existingPool.Timestamp = trade.Timestamp;
                existingPool.Protocol = trade.Protocol;

                if (trade.AF.HasValue) existingPool.AF = trade.AF.Value;
                if (trade.BF.HasValue) existingPool.BF = trade.BF.Value;


                // Check if we need to enrich the pool with missing data
                if (string.IsNullOrEmpty(existingPool.ApprovalProgramHash))
                {
                    var enrichedPool = await TryEnrichPoolWithProcessor(existingPool, cancellationToken);
                    if (enrichedPool != null)
                    {
                        existingPool = enrichedPool;
                    }
                }

                await StorePoolAsync(existingPool, cancellationToken);

                _logger.LogDebug("Updated pool from trade: {poolAddress}_{poolAppId}_{protocol}",
                    trade.PoolAddress, trade.PoolAppId, trade.Protocol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update pool from trade {txId}", trade.TxId);
            }
        }

        public async Task UpdatePoolFromLiquidity(Liquidity liquidity, CancellationToken cancellationToken)
        {
            // Only update from confirmed liquidity updates
            if (liquidity.TxState != TxState.Confirmed)
            {
                _logger.LogDebug("Skipping pool update from unconfirmed liquidity {txId}", liquidity.TxId);
                return;
            }

            await EnsureInitialized(cancellationToken);

            try
            {
                var poolId = GeneratePoolId(liquidity.PoolAddress);
                var existingPool = _poolsCache.TryGetValue(poolId, out var pool) ? pool : null;

                // If pool doesn't exist, create a new one from liquidity data
                if (existingPool == null)
                {
                    var newPool = await CreatePoolFromLiquidity(liquidity);

                    // Check if we need to load full pool data using pool processor
                    if (string.IsNullOrEmpty(newPool.ApprovalProgramHash))
                    {
                        var enrichedPool = await TryEnrichPoolWithProcessor(newPool, cancellationToken);
                        if (enrichedPool != null)
                        {
                            newPool = enrichedPool;
                        }
                    }

                    await StorePoolAsync(newPool, cancellationToken);
                    _logger.LogInformation("Created new pool from liquidity: {poolAddress}_{poolAppId}_{protocol}",
                        liquidity.PoolAddress, liquidity.PoolAppId, liquidity.Protocol);
                    return;
                }

                // Only update if timestamp is equal or larger
                if (liquidity.Timestamp.HasValue && existingPool.Timestamp.HasValue &&
                    liquidity.Timestamp.Value < existingPool.Timestamp.Value)
                {
                    _logger.LogDebug("Skipping pool update - liquidity timestamp {liquidityTime} is older than pool timestamp {poolTime}",
                        liquidity.Timestamp.Value, existingPool.Timestamp.Value);
                    return;
                }

                existingPool.AssetIdA = liquidity.AssetIdA;
                existingPool.AssetIdB = liquidity.AssetIdB;
                existingPool.AssetIdLP = liquidity.AssetIdLP;
                existingPool.A = liquidity.A;
                existingPool.B = liquidity.B;
                if (liquidity.L > 0)
                    existingPool.L = liquidity.L;
                existingPool.Timestamp = liquidity.Timestamp;
                existingPool.Protocol = liquidity.Protocol;
                if (liquidity.AF.HasValue) existingPool.AF = liquidity.AF.Value;
                if (liquidity.BF.HasValue) existingPool.BF = liquidity.BF.Value;

                // Check if we need to enrich the pool with missing data
                if (string.IsNullOrEmpty(existingPool.ApprovalProgramHash))
                {
                    var enrichedPool = await TryEnrichPoolWithProcessor(existingPool, cancellationToken);
                    if (enrichedPool != null)
                    {
                        existingPool = enrichedPool;
                    }
                }

                await StorePoolAsync(existingPool, cancellationToken);

                _logger.LogDebug("Updated pool from liquidity: {poolAddress}_{poolAppId}_{protocol}",
                    liquidity.PoolAddress, liquidity.PoolAppId, liquidity.Protocol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update pool from liquidity {txId}", liquidity.TxId);
            }
        }
        /// <summary>
        /// Retrieves a filtered list of liquidity pools based on the specified criteria.
        /// </summary>
        /// <remarks>If both <paramref name="assetIdA"/> and <paramref name="assetIdB"/> are provided, the
        /// method filters pools where the pair of assets matches either order (e.g., asset A and asset B, or asset B
        /// and asset A). If no filters are specified, all available pools are returned, up to the specified <paramref
        /// name="size"/>.</remarks>
        /// <param name="assetIdA">The ID of the first asset in the pool. Can be <see langword="null"/> to ignore this filter.</param>
        /// <param name="assetIdB">The ID of the second asset in the pool. Can be <see langword="null"/> to ignore this filter.</param>
        /// <param name="address">The address of the pool. Can be <see langword="null"/> or empty to ignore this filter.</param>
        /// <param name="protocol">The decentralized exchange (DEX) protocol to filter by. Can be <see langword="null"/> to ignore this filter.</param>
        /// <param name="size">The maximum number of pools to return. Must be a positive integer. Defaults to 100.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A list of <see cref="Pool"/> objects that match the specified criteria. The list is sorted by timestamp in
        /// descending order.</returns>
        public async Task<List<Pool>> GetPoolsAsync(ulong? assetIdA, ulong? assetIdB, string? address, DEXProtocol? protocol = null, int size = 100, CancellationToken cancellationToken = default)
        {
            await EnsureInitialized(cancellationToken);

            var filteredPools = _poolsCache.Values.AsEnumerable();

            if (assetIdA.HasValue && assetIdB.HasValue)
            {
                filteredPools = filteredPools.Where(p => (p.AssetIdA == assetIdA.Value && p.AssetIdB == assetIdB.Value) || (p.AssetIdB == assetIdA.Value && p.AssetIdA == assetIdB.Value));
            }
            if (!string.IsNullOrEmpty(address))
            {
                filteredPools = filteredPools.Where(p => p.PoolAddress == address);
            }

            // Filter by protocol if specified
            if (protocol.HasValue)
            {
                filteredPools = filteredPools.Where(p => p.Protocol == protocol.Value);
            }

            // Sort by timestamp descending and limit size
            filteredPools = filteredPools
                .OrderByDescending(p => p.Timestamp ?? DateTimeOffset.MinValue)
                .Take(size);

            return filteredPools.ToList();
        }

        public async Task<int> GetPoolCountAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitialized(cancellationToken);
            return _poolsCache.Count;
        }

        private async Task EnsureInitialized(CancellationToken cancellationToken)
        {
            if (!_isInitialized)
            {
                await InitializeAsync(cancellationToken);
            }
        }

        private string GeneratePoolId(string poolAddress)
        {
            return $"{poolAddress}";
            //return $"{poolAddress}_{poolAppId}_{protocol}";
        }

        private async Task<Pool> CreatePoolFromTrade(Trade trade)
        {
            var processor = GetPoolProcessor(trade.Protocol);
            if (processor != null)
            {
                var pool = await processor.LoadPoolAsync(trade.PoolAddress, trade.PoolAppId);
                if (pool != null)
                {
                    _poolsCache[pool.PoolAddress] = pool; // Update cache
                    var cancellationTokenSource = new CancellationTokenSource();
                    await UpdatePoolFromTrade(trade, cancellationTokenSource.Token);
                    return _poolsCache[pool.PoolAddress];
                }
            }
            return new Pool
            {
                PoolAddress = trade.PoolAddress,
                PoolAppId = trade.PoolAppId,
                A = trade.A,
                B = trade.B,
                L = trade.L,
                Protocol = trade.Protocol,
                Timestamp = trade.Timestamp
            };
        }

        private async Task<Pool> CreatePoolFromLiquidity(Liquidity liquidity)
        {
            var processor = GetPoolProcessor(liquidity.Protocol);
            if (processor != null)
            {
                var pool = await processor.LoadPoolAsync(liquidity.PoolAddress, liquidity.PoolAppId);
                if (pool != null)
                {
                    _poolsCache[pool.PoolAddress] = pool; // Update cache
                    var cancellationTokenSource = new CancellationTokenSource();
                    await UpdatePoolFromLiquidity(liquidity, cancellationTokenSource.Token);
                    return _poolsCache[pool.PoolAddress];
                }
            }
            return new Pool
            {
                PoolAddress = liquidity.PoolAddress,
                PoolAppId = liquidity.PoolAppId,
                AssetIdA = liquidity.AssetIdA,
                AssetIdB = liquidity.AssetIdB,
                AssetIdLP = liquidity.AssetIdLP,
                A = liquidity.A,
                B = liquidity.B,
                L = liquidity.L,
                Protocol = liquidity.Protocol,
                Timestamp = liquidity.Timestamp
            };
        }

        private async Task PublishPoolUpdateToHub(Pool pool, CancellationToken cancellationToken)
        {
            try
            {
                if(_hubContext == null)
                {
                    _logger.LogWarning("SignalR hub context is not initialized, cannot publish pool update");
                    return;
                }
                await _hubContext.Clients.All.SendAsync("PoolUpdated", pool, cancellationToken);
                _logger.LogDebug("Published pool update to SignalR hub: {poolAddress}_{poolAppId}_{protocol}",
                    pool.PoolAddress, pool.PoolAppId, pool.Protocol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish pool update to SignalR hub");
            }
        }

        /// <summary>
        /// Tries to enrich a pool with missing data by running the appropriate pool processor
        /// </summary>
        private async Task<Pool?> TryEnrichPoolWithProcessor(Pool pool, CancellationToken cancellationToken)
        {
            try
            {
                var processor = GetPoolProcessor(pool.Protocol);
                if (processor == null)
                {
                    _logger.LogWarning("No pool processor found for protocol {protocol}", pool.Protocol);
                    return null;
                }

                _logger.LogInformation("Enriching pool {poolAddress}_{poolAppId}_{protocol} using pool processor",
                    pool.PoolAddress, pool.PoolAppId, pool.Protocol);

                // Use the pool processor to load complete pool data
                var enrichedPool = await processor.LoadPoolAsync(pool.PoolAddress, pool.PoolAppId);

                _logger.LogInformation("Successfully enriched pool {poolAddress}_{poolAppId}_{protocol} with approvalProgramHash: {hash}",
                    pool.PoolAddress, pool.PoolAppId, pool.Protocol, enrichedPool?.ApprovalProgramHash);

                return enrichedPool;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enrich pool {poolAddress}_{poolAppId}_{protocol} using pool processor: {error}",
                    pool.PoolAddress, pool.PoolAppId, pool.Protocol, ex.Message);

                // Try with alternative processors if the main one fails
                if (pool.Protocol == DEXProtocol.Tiny)
                {
                    try
                    {
                        var pactProcessor = GetPoolProcessor(DEXProtocol.Pact);
                        if (pactProcessor != null)
                        {
                            _logger.LogInformation("Trying to enrich Tiny pool {poolAddress}_{poolAppId} with Pact processor",
                                pool.PoolAddress, pool.PoolAppId);

                            var enrichedPool = await pactProcessor.LoadPoolAsync(pool.PoolAddress, pool.PoolAppId);
                            // Update protocol to reflect actual processor used
                            if (enrichedPool != null)
                            {
                                enrichedPool.Protocol = DEXProtocol.Pact;
                            }
                            return enrichedPool;
                        }
                    }
                    catch (Exception pactEx)
                    {
                        _logger.LogWarning(pactEx, "Failed to enrich pool {poolAddress}_{poolAppId} with Pact processor: {error}",
                            pool.PoolAddress, pool.PoolAppId, pactEx.Message);
                    }
                }
                else if (pool.Protocol == DEXProtocol.Pact)
                {
                    try
                    {
                        var tinyProcessor = GetPoolProcessor(DEXProtocol.Tiny);
                        if (tinyProcessor != null)
                        {
                            _logger.LogInformation("Trying to enrich Pact pool {poolAddress}_{poolAppId} with Tiny processor",
                                pool.PoolAddress, pool.PoolAppId);

                            var enrichedPool = await tinyProcessor.LoadPoolAsync(pool.PoolAddress, pool.PoolAppId);
                            // Update protocol to reflect actual processor used
                            if (enrichedPool != null)
                            {
                                enrichedPool.Protocol = DEXProtocol.Tiny;
                            }
                            return enrichedPool;
                        }
                    }
                    catch (Exception tinyEx)
                    {
                        _logger.LogWarning(tinyEx, "Failed to enrich pool {poolAddress}_{poolAppId} with Tiny processor: {error}",
                            pool.PoolAddress, pool.PoolAppId, tinyEx.Message);
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Gets the appropriate pool processor for the specified protocol
        /// </summary>
        public IPoolProcessor? GetPoolProcessor(DEXProtocol protocol)
        {
            return protocol switch
            {
                DEXProtocol.Pact => _serviceProvider.GetService<PactPoolProcessor>(),
                DEXProtocol.Tiny => _serviceProvider.GetService<TinyPoolProcessor>(),
                DEXProtocol.Biatec => _serviceProvider.GetService<BiatecPoolProcessor>(),
                _ => null
            };
        }
    }
}