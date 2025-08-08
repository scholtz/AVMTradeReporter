using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
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
        private readonly IDatabase? _redisDatabase;
        private readonly AppConfiguration _appConfig;

        // In-memory cache for pools
        private readonly ConcurrentDictionary<string, Pool> _poolsCache = new();
        private readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
        private bool _isInitialized = false;

        public PoolRepository(
            ElasticsearchClient elasticClient,
            ILogger<PoolRepository> logger,
            IHubContext<BiatecScanHub> hubContext,
            IOptions<AppConfiguration> appConfig,
            IDatabase? redisDatabase = null
            )
        {
            _elasticClient = elasticClient;
            _logger = logger;
            _hubContext = hubContext;
            _redisDatabase = redisDatabase;
            _appConfig = appConfig.Value;

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
                                var poolId = GeneratePoolId(pool.PoolAddress, pool.PoolAppId, pool.Protocol);
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
                var searchResponse = await _elasticClient.SearchAsync<Pool>(s => s
                    .Indices("pools")
                    .Size(10000), cancellationToken);

                if (searchResponse.IsValidResponse)
                {
                    int loadedCount = 0;
                    foreach (var pool in searchResponse.Documents)
                    {
                        var poolId = GeneratePoolId(pool.PoolAddress, pool.PoolAppId, pool.Protocol);
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
                    var poolId = GeneratePoolId(pool.PoolAddress, pool.PoolAppId, pool.Protocol);
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

            var response = await _elasticClient.Indices.PutIndexTemplateAsync(templateRequest);
            Console.WriteLine($"Pool template created: {response.IsValidResponse}");
        }

        public async Task<Pool?> GetPoolAsync(string poolAddress, ulong poolAppId, DEXProtocol protocol, CancellationToken cancellationToken)
        {
            await EnsureInitialized(cancellationToken);

            var poolId = GeneratePoolId(poolAddress, poolAppId, protocol);
            return _poolsCache.TryGetValue(poolId, out var pool) ? pool : null;
        }

        public async Task<bool> StorePoolAsync(Pool pool, CancellationToken cancellationToken)
        {
            await EnsureInitialized(cancellationToken);

            try
            {
                var poolId = GeneratePoolId(pool.PoolAddress, pool.PoolAppId, pool.Protocol);

                // Update in-memory cache
                _poolsCache.AddOrUpdate(poolId, pool, (key, oldValue) => pool);

                // Save to Redis asynchronously
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_redisDatabase != null && _appConfig.Redis.Enabled)
                        {
                            var redisKey = $"{_appConfig.Redis.KeyPrefix}{poolId}";
                            var poolJson = JsonSerializer.Serialize(pool);
                            await _redisDatabase.StringSetAsync(redisKey, poolJson);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to save pool to Redis: {poolId}", poolId);
                    }
                }, cancellationToken);

                // Save to Elasticsearch asynchronously
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var response = await _elasticClient.IndexAsync(pool, idx => idx
                            .Index("pools")
                            .Id(poolId), cancellationToken);

                        if (!response.IsValidResponse)
                        {
                            _logger.LogError("Failed to store pool in Elasticsearch {poolId}: {error}", poolId, response.DebugInformation);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to store pool in Elasticsearch: {poolId}", poolId);
                    }
                }, cancellationToken);

                _logger.LogDebug("Pool updated in memory: {poolId}", poolId);

                // Publish pool update to SignalR hub
                await PublishPoolUpdateToHub(pool, cancellationToken);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store pool");
                return false;
            }
        }

        public async Task UpdatePoolFromTrade(Trade trade, CancellationToken cancellationToken)
        {
            // Only update from confirmed trades
            if (trade.TradeState != TradeState.Confirmed)
            {
                _logger.LogDebug("Skipping pool update from unconfirmed trade {txId}", trade.TxId);
                return;
            }

            await EnsureInitialized(cancellationToken);

            try
            {
                var poolId = GeneratePoolId(trade.PoolAddress, trade.PoolAppId, trade.Protocol);
                var existingPool = _poolsCache.TryGetValue(poolId, out var pool) ? pool : null;

                // If pool doesn't exist, create a new one from trade data
                if (existingPool == null)
                {
                    var newPool = CreatePoolFromTrade(trade);
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
            if (liquidity.TxState != TradeState.Confirmed)
            {
                _logger.LogDebug("Skipping pool update from unconfirmed liquidity {txId}", liquidity.TxId);
                return;
            }

            await EnsureInitialized(cancellationToken);

            try
            {
                var poolId = GeneratePoolId(liquidity.PoolAddress, liquidity.PoolAppId, liquidity.Protocol);
                var existingPool = _poolsCache.TryGetValue(poolId, out var pool) ? pool : null;

                // If pool doesn't exist, create a new one from liquidity data
                if (existingPool == null)
                {
                    var newPool = CreatePoolFromLiquidity(liquidity);
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

                existingPool.A = liquidity.A;
                existingPool.B = liquidity.B;
                if (liquidity.L > 0)
                    existingPool.L = liquidity.L;
                existingPool.Timestamp = liquidity.Timestamp;

                await StorePoolAsync(existingPool, cancellationToken);

                _logger.LogDebug("Updated pool from liquidity: {poolAddress}_{poolAppId}_{protocol}",
                    liquidity.PoolAddress, liquidity.PoolAppId, liquidity.Protocol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update pool from liquidity {txId}", liquidity.TxId);
            }
        }

        public async Task<List<Pool>> GetPoolsAsync(DEXProtocol? protocol = null, int size = 100, CancellationToken cancellationToken = default)
        {
            await EnsureInitialized(cancellationToken);

            var pools = _poolsCache.Values.AsEnumerable();

            // Filter by protocol if specified
            if (protocol.HasValue)
            {
                pools = pools.Where(p => p.Protocol == protocol.Value);
            }

            // Sort by timestamp descending and limit size
            pools = pools
                .OrderByDescending(p => p.Timestamp ?? DateTimeOffset.MinValue)
                .Take(size);

            return pools.ToList();
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

        private string GeneratePoolId(string poolAddress, ulong poolAppId, DEXProtocol protocol)
        {
            return $"{poolAddress}";
            //return $"{poolAddress}_{poolAppId}_{protocol}";
        }

        private Pool CreatePoolFromTrade(Trade trade)
        {
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

        private Pool CreatePoolFromLiquidity(Liquidity liquidity)
        {
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
                await _hubContext.Clients.All.SendAsync("PoolUpdated", pool, cancellationToken);
                _logger.LogDebug("Published pool update to SignalR hub: {poolAddress}_{poolAppId}_{protocol}",
                    pool.PoolAddress, pool.PoolAppId, pool.Protocol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish pool update to SignalR hub");
            }
        }
    }
}