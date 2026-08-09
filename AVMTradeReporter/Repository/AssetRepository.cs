using Algorand.Algod;
using Algorand.KMD;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Processors.Pool;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using AVMTradeReporter.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using AVMTradeReporter.Model.Configuration;

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
        private const string RedisKeyBase = "asset:";
        private readonly AppConfiguration? _appConfig;
        private string RedisKeyPrefix => (_appConfig?.Redis?.EnvironmentKeyPrefix ?? string.Empty) + RedisKeyBase;

        public AssetRepository(
            IDefaultApi algod,
            ILogger<AssetRepository> logger,
            IDatabase? redisDatabase = null,
            IHubContext<BiatecScanHub>? hubContext = null,
            IOptions<AppConfiguration>? appConfig = null)
        {
            _algod = algod;
            _logger = logger;
            _redisDatabase = redisDatabase;
            _hubContext = hubContext;
            _appConfig = appConfig?.Value;
        }

        private void EnsureStabilityIndexInitialized(BiatecAsset asset)
        {
            if (asset == null) return;

            // If already set, keep it (allows overriding in Redis).
            if (asset.StabilityIndex != 0) return;

            if (_appConfig?.AssetStabilityIndex != null && _appConfig.AssetStabilityIndex.TryGetValue(asset.Index, out var idx))
            {
                asset.StabilityIndex = idx;
            }
            else
            {
                asset.StabilityIndex = 0;
            }
        }

        public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
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
                            var prefix = RedisKeyPrefix;
                            foreach (var key in server.Keys(pattern: prefix + "*"))
                            {
                                // Only "{prefix}{numeric asset id}" keys hold asset records. Other
                                // feature keys share the "asset:" namespace (asset:top:summary,
                                // asset:tvl:hourly:{h}, asset:timeseries:7d:{id}) and deserializing
                                // them as BiatecAsset yields a bogus Index=0 record that would
                                // overwrite the native token entry in the cache.
                                var suffix = key.ToString().Substring(prefix.Length);
                                if (!ulong.TryParse(suffix, out _)) continue;
                                var value = await _redisDatabase.StringGetAsync(key);
                                if (value.HasValue)
                                {
                                    try
                                    {
                                        var asset = JsonSerializer.Deserialize<BiatecAsset>((string)value!);
                                        if (asset != null)
                                        {
                                            if (asset.Timestamp == null)
                                            {
                                                asset.Timestamp = DateTimeOffset.UtcNow;
                                            }
                                            EnsureStabilityIndexInitialized(asset);
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
                            Name = string.IsNullOrWhiteSpace(_appConfig?.NativeTokenName) ? "Algorand" : _appConfig.NativeTokenName,
                            Url = "https://www.algorand.com",
                            MetadataHash = null,
                            Manager = null,
                            Reserve = null,
                            Freeze = null,
                            Clawback = null
                        },
                        Timestamp = DateTimeOffset.UtcNow
                    };
                    EnsureStabilityIndexInitialized(algo);
                    _assetCache[0] = algo;
                    return algo;
                }

                if (_assetCache.TryGetValue(assetId, out var cached))
                {
                    if (cached.Deleted) return null;
                    EnsureStabilityIndexInitialized(cached);
                    return cached;
                }

                // Not in memory, load from algod
                var asset = await _algod.GetAssetByIDAsync(cancellationToken, assetId);
                if (asset == null) return null;
                var assetToStore = Newtonsoft.Json.JsonConvert.DeserializeObject<BiatecAsset>(Newtonsoft.Json.JsonConvert.SerializeObject(asset) ?? throw new Exception($"Unable to serialize asset {asset.Index}")) ?? throw new Exception($"Unable to deserialize asset to biatec asset {asset.Index}");
                EnsureStabilityIndexInitialized(assetToStore);
                await SetAssetAsync(assetToStore, cancellationToken);
                return assetToStore;
            }
            catch (Algorand.ApiException apiEx) when (apiEx.StatusCode == 404)
            {
                // The ASA was destroyed on chain (asset ids are never reused on Algorand).
                // Cache a permanent tombstone so we don't hammer algod and spam the error
                // log on every subsequent lookup of the same id.
                _logger.LogWarning("Asset {AssetId} does not exist on chain (destroyed or invalid id); caching tombstone", assetId);
                var tombstone = new BiatecAsset
                {
                    Index = assetId,
                    Deleted = true,
                    Timestamp = DateTimeOffset.UtcNow,
                };
                _assetCache[assetId] = tombstone;
                await PersistToRedisAsync(tombstone, cancellationToken);
                return null;
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

            EnsureStabilityIndexInitialized(asset);

            if (asset.Timestamp == null)
            {
                asset.Timestamp = DateTimeOffset.UtcNow;
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
                query = _assetCache.Where(kvp => ids.Contains(kvp.Key)).Select(kvp => kvp.Value).Where(a => !a.Deleted);
            }
            else
            {
                // Without an explicit id list this is the general browse/search listing (Assets page),
                // so hide assets that aren't actually traded in any pool (e.g. an LP token's own ASA).
                query = query.Where(a => a.PoolsCount > 0);
            }


            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLowerInvariant();
                if (s == "utility")
                {
                    query = query.Where(a => a.StabilityIndex == 0);
                }
                else
                if (s == "stable")
                {
                    query = query.Where(a => a.StabilityIndex > 0);
                }
                else
                {
                    if (ulong.TryParse(s, out var asset))
                    {
                        query = query.Where(a => a.Index == asset || (a.Params?.Name?.ToLowerInvariant().Contains(s) ?? false) || (a.Params?.UnitName?.ToLowerInvariant().Contains(s) ?? false));
                    }
                    else
                    {
                        query = query.Where(a => (a.Params?.Name?.ToLowerInvariant().Contains(s) ?? false) || (a.Params?.UnitName?.ToLowerInvariant().Contains(s) ?? false));
                    }
                }
            }

            return query.OrderByDescending(a => a.TVL_USD).Skip(offset).Take(size).ToArray();
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
