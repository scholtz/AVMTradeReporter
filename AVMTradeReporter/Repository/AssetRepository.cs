using Algorand.Algod;
using Algorand.KMD;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Processors.Pool;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using AVMTradeReporter.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AVMTradeReporter.Repository
{
    public class AssetRepository : IAssetRepository
    {
        private readonly IDefaultApi _algod;
        private readonly ILogger<AssetRepository> _logger;
        private readonly IDatabase? _redisDatabase;
        private static readonly ConcurrentDictionary<ulong, BiatecAsset> _assetCache = new();
        private readonly IHubContext<BiatecScanHub>? _hubContext;
        private static bool _initialized = false;
        private static readonly SemaphoreSlim _initLock = new(1, 1);
        private const string RedisKeyPrefix = "asset:";

        public AssetRepository(
            IDefaultApi algod,
            ILogger<AssetRepository> logger,
            IDatabase? redisDatabase = null,
            IHubContext<BiatecScanHub>? hubContext = null)
        {
            _algod = algod;
            _logger = logger;
            _redisDatabase = redisDatabase;
            _hubContext = hubContext;
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (_initialized) return;
            await _initLock.WaitAsync(cancellationToken);
            try
            {
                if (_initialized) return;
                if (_redisDatabase != null)
                {
                    _logger.LogInformation("Loading assets from Redis into memory cache...");
                    try
                    {
                        var server = GetServer();
                        if (server != null)
                        {
                            int loaded = 0;
                            foreach (var key in server.Keys(pattern: RedisKeyPrefix + "*"))
                            {
                                var value = await _redisDatabase.StringGetAsync(key);
                                if (value.HasValue)
                                {
                                    try
                                    {
                                        var asset = JsonSerializer.Deserialize<BiatecAsset>(value!);
                                        if (asset != null)
                                        {
                                            if (asset.Timestamp == null)
                                            {
                                                asset.Timestamp = DateTimeOffset.UtcNow;
                                            }
                                            _assetCache[asset.Index] = asset;
                                            loaded++;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "Failed to deserialize asset from Redis key {key}", key);
                                    }
                                }
                            }
                            _logger.LogInformation("Loaded {count} assets from Redis into memory cache", loaded);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error while loading assets from Redis");
                    }
                }
                _initialized = true;
            }
            finally
            {
                _initLock.Release();
            }
        }

        private StackExchange.Redis.IServer? GetServer()
        {
            try
            {
                if (_redisDatabase == null) return null;
                var multiplexer = _redisDatabase.Multiplexer;
                var endpoint = multiplexer.GetEndPoints().FirstOrDefault();
                if (endpoint == null) return null;
                return multiplexer.GetServer(endpoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get Redis server for scanning asset keys");
                return null;
            }
        }

        public async Task<BiatecAsset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureInitializedAsync(cancellationToken);

                if (assetId == 0)
                {
                    if (_assetCache.TryGetValue(0, out var native)) return native;
                    var algo = new BiatecAsset()
                    {
                        Index = 0,
                        Params = new Algorand.Algod.Model.AssetParams()
                        {
                            Total = 10_000_000_000,
                            Decimals = 6,
                            DefaultFrozen = false,
                            UnitName = "ALGO",
                            Name = "Algorand",
                            Url = "https://www.algorand.com",
                            MetadataHash = null,
                            Manager = null,
                            Reserve = null,
                            Freeze = null,
                            Clawback = null
                        },
                        Timestamp = DateTimeOffset.UtcNow
                    };
                    _assetCache[0] = algo;
                    return algo;
                }

                if (_assetCache.TryGetValue(assetId, out var cached))
                {
                    return cached;
                }

                // Not in memory, load from algod
                var asset = await _algod.GetAssetByIDAsync(cancellationToken, assetId);
                if (asset != null)
                {
                    _assetCache[assetId] = Newtonsoft.Json.JsonConvert.DeserializeObject<BiatecAsset>(Newtonsoft.Json.JsonConvert.SerializeObject(asset) ?? throw new Exception($"Unable to serialize asset {asset.Index}")) ?? throw new Exception($"Unable to deserialize asset to biatec asset {asset.Index}");
                    await SetAssetAsync(_assetCache[assetId], cancellationToken); // fire and forget
                }
                return _assetCache[assetId];
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve asset {AssetId}", assetId);
                return null;
            }
        }

        public async Task SetAssetAsync(BiatecAsset asset, CancellationToken cancellationToken = default)
        {
            if (asset == null) return;
            if (_assetCache.ContainsKey(asset.Index))
            {
                var cached = _assetCache[asset.Index];
                if (asset.PriceUSD != cached.PriceUSD || asset.TVL_USD != cached.TVL_USD)
                {
                    asset.Timestamp = DateTimeOffset.UtcNow;
                }
            }
            _assetCache[asset.Index] = asset;

            BiatecScanHub.RecentAssetUpdates.Enqueue(asset);
            while (BiatecScanHub.RecentAssetUpdates.Count > 50)
            {
                BiatecScanHub.RecentAssetUpdates.TryDequeue(out _);
            }
            await EnsureInitializedAsync(cancellationToken);
            await PersistToRedisAsync(asset, cancellationToken);
            await PublishToHubAsync(asset, cancellationToken);
        }

        private async Task PublishToHubAsync(BiatecAsset asset, CancellationToken cancellationToken)
        {
            try
            {
                if (asset == null) throw new ArgumentNullException(nameof(asset));
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

                        if (BiatecScanHub.ShouldSendAssetToUser(asset, filter))
                        {
                            subscribedClientsConnections.Add(userId);
                        }
                    }
                    await _hubContext.Clients.Users(subscribedClientsConnections).SendAsync(BiatecScanHub.Subscriptions.ASSET, asset, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish asset {AssetId} to hub", asset.Index);
            }
        }

        public async Task<IEnumerable<BiatecAsset>> GetAssetsAsync(IEnumerable<ulong>? ids, string? search, int offset, int size, CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken);

            IEnumerable<BiatecAsset> query = _assetCache.Values;

            if (ids != null && ids.Any())
            {
                var missing = ids.Where(id => !_assetCache.ContainsKey(id)).ToArray();
                foreach (var id in missing)
                {
                    await GetAssetAsync(id, cancellationToken);
                }
                query = _assetCache.Where(kvp => ids.Contains(kvp.Key)).Select(kvp => kvp.Value);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLowerInvariant();
                query = query.Where(a => (a.Params?.Name?.ToLowerInvariant().Contains(s) ?? false) || (a.Params?.UnitName?.ToLowerInvariant().Contains(s) ?? false));
            }

            return query.OrderBy(a => a.Index).Skip(offset).Take(size).ToArray();
        }

        private async Task PersistToRedisAsync(BiatecAsset asset, CancellationToken cancellationToken)
        {
            if (_redisDatabase == null) return;
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(asset);
                await _redisDatabase.StringSetAsync(RedisKeyPrefix + asset.Index, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist asset {AssetId} to Redis", asset.Index);
            }
        }
    }
}
