using Algorand.Algod;
using Algorand.KMD;
using AVMTradeReporter.Processors.Pool;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AVMTradeReporter.Repository
{
    public class AssetRepository : IAssetRepository
    {
        private readonly IDefaultApi _algod;
        private readonly ILogger<AssetRepository> _logger;
        private readonly IDatabase? _redisDatabase;
        private static readonly ConcurrentDictionary<ulong, Algorand.Algod.Model.Asset> _assetCache = new();
        private static bool _initialized = false;
        private static readonly SemaphoreSlim _initLock = new(1, 1);
        private const string RedisKeyPrefix = "asset:";

        public AssetRepository(
            IDefaultApi algod,
            ILogger<AssetRepository> logger,
            IDatabase? redisDatabase = null)
        {
            _algod = algod;
            _logger = logger;
            _redisDatabase = redisDatabase;
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
                                        var asset = System.Text.Json.JsonSerializer.Deserialize<Algorand.Algod.Model.Asset>(value!);
                                        if (asset != null)
                                        {
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

        public async Task<Algorand.Algod.Model.Asset?> GetAssetAsync(ulong assetId, CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureInitializedAsync(cancellationToken);

                if (assetId == 0)
                {
                    if (_assetCache.TryGetValue(0, out var native)) return native;
                    var algo = new Algorand.Algod.Model.Asset()
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
                        }
                    };
                    _assetCache[0] = algo;
                    return algo;
                }

                if (_assetCache.TryGetValue(assetId, out var cached))
                {
                    return cached;
                }

                var asset = await _algod.GetAssetByIDAsync(cancellationToken, assetId);
                if (asset != null)
                {
                    _assetCache[assetId] = asset;
                    _ = PersistToRedisAsync(assetId, asset);
                }
                return asset;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve asset {AssetId}", assetId);
                return null;
            }
        }

        public async Task SetAssetAsync(Algorand.Algod.Model.Asset asset, CancellationToken cancellationToken = default)
        {
            if (asset == null) return;
            await EnsureInitializedAsync(cancellationToken);
            _assetCache[asset.Index] = asset;
            await PersistToRedisAsync(asset.Index, asset);
        }

        public async Task<IEnumerable<Algorand.Algod.Model.Asset>> GetAssetsAsync(IEnumerable<ulong>? ids, string? search, int size, CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken);

            IEnumerable<Algorand.Algod.Model.Asset> query = _assetCache.Values;

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

            return query.OrderBy(a => a.Index).Take(size).ToArray();
        }

        private async Task PersistToRedisAsync(ulong assetId, Algorand.Algod.Model.Asset asset)
        {
            if (_redisDatabase == null) return;
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(asset);
                await _redisDatabase.StringSetAsync(RedisKeyPrefix + assetId, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist asset {AssetId} to Redis", assetId);
            }
        }
    }
}
