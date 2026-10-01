using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVMTradeReporter.Services.CoinGecko
{
    public enum CoinGeckoOutcome
    {
        Ok,
        /// <summary>The integration is turned off or its data source (Elasticsearch / indexer watermark) is not ready - HTTP 503.</summary>
        Unavailable,
        /// <summary>The request is invalid (bad range, range too large, beyond latest-block) - HTTP 400.</summary>
        BadRequest,
        /// <summary>Unknown asset / pair - HTTP 404.</summary>
        NotFound,
    }

    public sealed record CoinGeckoResult<T>(CoinGeckoOutcome Outcome, T? Value = default, string? Error = null) where T : class
    {
        public static CoinGeckoResult<T> Ok(T value) => new(CoinGeckoOutcome.Ok, value);
        public static CoinGeckoResult<T> Fail(CoinGeckoOutcome outcome, string error) => new(outcome, null, error);
    }

    public interface ICoinGeckoService
    {
        Task<CoinGeckoResult<CoinGeckoLatestBlockResponse>> GetLatestBlockAsync(CancellationToken cancellationToken);
        Task<CoinGeckoResult<CoinGeckoAssetResponse>> GetAssetAsync(string? id, CancellationToken cancellationToken);
        Task<CoinGeckoResult<CoinGeckoPairResponse>> GetPairAsync(string? id, CancellationToken cancellationToken);

        /// <summary>The UTF-8 JSON body of the <c>/events</c> response (cached bytes are served as they are).</summary>
        Task<CoinGeckoResult<byte[]>> GetEventsJsonAsync(ulong fromBlock, ulong toBlock, CancellationToken cancellationToken);
    }

    /// <summary>
    /// GeckoTerminal (CoinGecko) non-EVM DEX integration, "GeckoTerminal Integration API Standards v0.1".
    /// </summary>
    /// <remarks>
    /// Efficiency model: <c>/asset</c> and <c>/pair</c> never touch Elasticsearch (in-memory asset / pool caches);
    /// <c>/latest-block</c> is a memory read; <c>/events</c> costs two filtered, blockId-sorted Elasticsearch queries
    /// per not yet seen range and is then served from memory / Redis (a range below latest-block is immutable), with
    /// identical concurrent requests collapsed into one query.
    /// </remarks>
    public sealed class CoinGeckoService : ICoinGeckoService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static readonly TimeSpan MissingPoolRefreshInterval = TimeSpan.FromSeconds(5);

        private readonly CoinGeckoConfiguration _config;
        private readonly RedisConfiguration _redisConfig;
        private readonly IIndexedBlockTracker _tracker;
        private readonly ICoinGeckoEventSource _source;
        private readonly IPoolRepository _poolRepository;
        private readonly IAssetRepository _assetRepository;
        private readonly IDatabase? _redis;
        private readonly ILogger<CoinGeckoService> _logger;
        private readonly TimeProvider _time;

        private readonly MemoryCache _eventsCache;
        private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _inFlight = new();
        private readonly ConcurrentDictionary<ulong, (CoinGeckoAsset? Asset, DateTimeOffset Expires)> _assets = new();
        private readonly SemaphoreSlim _snapshotLock = new(1, 1);
        private volatile PoolSnapshot _snapshot = new(new Dictionary<ulong, PairInfo>(), new Dictionary<ulong, DateTimeOffset>(), new HashSet<ulong>(), DateTimeOffset.MinValue);

        /// <param name="Pairs">Published pairs by pool application id.</param>
        /// <param name="Unresolved">Published pools whose asset decimals could not be resolved, with the time they were first seen so (transient for <see cref="CoinGeckoConfiguration.UnresolvedPoolGraceMinutes"/>, then skipped).</param>
        /// <param name="AssetIds">Every asset of a published pool: the only assets <c>/asset</c> answers for.</param>
        private sealed record PoolSnapshot(Dictionary<ulong, PairInfo> Pairs, Dictionary<ulong, DateTimeOffset> Unresolved, HashSet<ulong> AssetIds, DateTimeOffset LoadedAt);

        /// <summary>Pair lookup result: <see cref="Transient"/> means "exists but cannot be described right now - try again", not "unknown".</summary>
        private readonly record struct PairLookup(PairInfo? Pair, bool Transient);

        /// <summary>Data that is expected to exist could not be read right now; the request must fail (503) instead of answering incompletely.</summary>
        private sealed class TransientDataException : Exception
        {
            public TransientDataException(string message) : base(message) { }
        }

        public CoinGeckoService(
            IOptions<AppConfiguration> options,
            IIndexedBlockTracker tracker,
            ICoinGeckoEventSource source,
            IPoolRepository poolRepository,
            IAssetRepository assetRepository,
            IServiceProvider services,
            ILogger<CoinGeckoService> logger,
            TimeProvider? timeProvider = null)
        {
            _config = options.Value.CoinGecko;
            _redisConfig = options.Value.Redis;
            _tracker = tracker;
            _source = source;
            _poolRepository = poolRepository;
            _assetRepository = assetRepository;
            _redis = _redisConfig.Enabled ? services.GetService<IDatabase>() : null;
            _logger = logger;
            _time = timeProvider ?? TimeProvider.System;
            _eventsCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = Math.Max(1, _config.EventsMemoryCacheMegabytes) * 1024L * 1024L });
        }

        // ---------------------------------------------------------------- latest block

        public async Task<CoinGeckoResult<CoinGeckoLatestBlockResponse>> GetLatestBlockAsync(CancellationToken cancellationToken)
        {
            if (!_config.Enabled) return CoinGeckoResult<CoinGeckoLatestBlockResponse>.Fail(CoinGeckoOutcome.Unavailable, "disabled");
            var latest = await _tracker.GetLatestAsync(cancellationToken);
            if (latest == null) return CoinGeckoResult<CoinGeckoLatestBlockResponse>.Fail(CoinGeckoOutcome.Unavailable, "The indexer has not completed a block yet");
            return CoinGeckoResult<CoinGeckoLatestBlockResponse>.Ok(new CoinGeckoLatestBlockResponse
            {
                Block = new CoinGeckoBlock { BlockNumber = latest.Round, BlockTimestamp = latest.UnixTimestamp },
            });
        }

        // ---------------------------------------------------------------- asset

        public async Task<CoinGeckoResult<CoinGeckoAssetResponse>> GetAssetAsync(string? id, CancellationToken cancellationToken)
        {
            if (!_config.Enabled) return CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.Unavailable, "disabled");
            if (!TryParseId(id, out var assetId)) return CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.BadRequest, "Query parameter 'id' must be an asset id (unsigned integer, ALGO = 0)");

            // Only assets of published pools are answered: bounds the cache and keeps an anonymous caller from making
            // the service look up arbitrary asset ids at algod.
            PoolSnapshot snapshot;
            try
            {
                snapshot = await GetSnapshotAsync(forceIfOlderThan: null, cancellationToken);
                if (!snapshot.AssetIds.Contains(assetId)) snapshot = await GetSnapshotAsync(MissingPoolRefreshInterval, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "CoinGecko asset {id} lookup failed", id);
                return CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.Unavailable, "The asset cannot be loaded right now, try again");
            }
            if (!snapshot.AssetIds.Contains(assetId)) return CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.NotFound, "Asset not found");

            var now = _time.GetUtcNow();
            if (_assets.TryGetValue(assetId, out var cached) && cached.Expires > now)
            {
                return cached.Asset == null
                    ? CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.NotFound, "Asset not found")
                    : CoinGeckoResult<CoinGeckoAssetResponse>.Ok(new CoinGeckoAssetResponse { Asset = cached.Asset });
            }

            BiatecAsset? stored;
            try
            {
                stored = await _assetRepository.GetAssetAsync(assetId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "CoinGecko asset {id} lookup failed", id);
                return CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.Unavailable, "The asset cannot be loaded right now, try again");
            }
            var asset = MapAsset(assetId, stored);
            // a destroyed / unreadable asset of a published pool is cached briefly so repeated lookups do not hit algod each time
            var ttl = asset == null ? TimeSpan.FromSeconds(Math.Min(30, _config.AssetCacheSeconds)) : TimeSpan.FromSeconds(_config.AssetCacheSeconds);
            _assets[assetId] = (asset, now + ttl);
            return asset == null
                ? CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.NotFound, "Asset not found")
                : CoinGeckoResult<CoinGeckoAssetResponse>.Ok(new CoinGeckoAssetResponse { Asset = asset });
        }

        public static CoinGeckoAsset? MapAsset(ulong assetId, BiatecAsset? asset)
        {
            var parameters = asset?.Params;
            if (asset == null || parameters == null || asset.Deleted) return null;
            var decimals = (int)parameters.Decimals;
            var symbol = (parameters.UnitName ?? string.Empty).Trim();
            var name = (parameters.Name ?? string.Empty).Trim();
            if (name.Length == 0) name = symbol.Length > 0 ? symbol : $"Asset {assetId}";
            if (symbol.Length == 0) symbol = name;

            var metadata = new Dictionary<string, string> { ["type"] = asset.Type.ToString() };
            if (!string.IsNullOrWhiteSpace(parameters.Url)) metadata["url"] = parameters.Url!;

            var total = parameters.Total.HasValue ? CoinGeckoMapper.Decimalize(parameters.Total.Value, decimals) : null;
            return new CoinGeckoAsset
            {
                Id = assetId.ToString(CultureInfo.InvariantCulture),
                Name = name,
                Symbol = symbol,
                Decimals = decimals,
                TotalSupply = total.HasValue ? CoinGeckoMapper.Format(total.Value) : null,
                Metadata = metadata,
            };
        }

        // ---------------------------------------------------------------- pair

        public async Task<CoinGeckoResult<CoinGeckoPairResponse>> GetPairAsync(string? id, CancellationToken cancellationToken)
        {
            if (!_config.Enabled) return CoinGeckoResult<CoinGeckoPairResponse>.Fail(CoinGeckoOutcome.Unavailable, "disabled");
            if (!TryParseId(id, out var appId)) return CoinGeckoResult<CoinGeckoPairResponse>.Fail(CoinGeckoOutcome.BadRequest, "Query parameter 'id' must be a pool application id (unsigned integer)");

            PairLookup lookup;
            try
            {
                lookup = await GetPairInfoAsync(appId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "CoinGecko pair {id} lookup failed", id);
                return CoinGeckoResult<CoinGeckoPairResponse>.Fail(CoinGeckoOutcome.Unavailable, "The pair cannot be loaded right now, try again");
            }
            if (lookup.Transient) return CoinGeckoResult<CoinGeckoPairResponse>.Fail(CoinGeckoOutcome.Unavailable, "The pair cannot be described right now, try again");
            if (lookup.Pair == null) return CoinGeckoResult<CoinGeckoPairResponse>.Fail(CoinGeckoOutcome.NotFound, "Pair not found");
            return CoinGeckoResult<CoinGeckoPairResponse>.Ok(new CoinGeckoPairResponse { Pair = ToPairDto(lookup.Pair) });
        }

        private static CoinGeckoPair ToPairDto(PairInfo pair) => new()
        {
            Id = pair.AppId.ToString(CultureInfo.InvariantCulture),
            DexKey = pair.Protocol.ToString().ToLowerInvariant(),
            Asset0Id = pair.Asset0Id.ToString(CultureInfo.InvariantCulture),
            Asset1Id = pair.Asset1Id.ToString(CultureInfo.InvariantCulture),
            FeeBps = pair.LpFee is >= 0 and < 1 ? CoinGeckoMapper.Normalize(decimal.Round(pair.LpFee.Value * 10000m, 4)) : null,
        };

        private async Task<PairLookup> GetPairInfoAsync(ulong appId, CancellationToken cancellationToken)
        {
            var snapshot = await GetSnapshotAsync(forceIfOlderThan: null, cancellationToken);
            if (snapshot.Pairs.TryGetValue(appId, out var pair)) return new PairLookup(pair, false);

            // A pool created after the snapshot was taken (or one whose decimals could not be read): refresh once
            // (rate limited) before answering "unknown".
            snapshot = await GetSnapshotAsync(MissingPoolRefreshInterval, cancellationToken);
            if (snapshot.Pairs.TryGetValue(appId, out pair)) return new PairLookup(pair, false);
            // Undescribable pools are retried for a while (a slow algod, a cold asset cache); a pool that stays undescribable
            // is then skipped instead of stalling the whole integration at its first block for ever.
            var transient = snapshot.Unresolved.TryGetValue(appId, out var since)
                && _time.GetUtcNow() - since < TimeSpan.FromMinutes(Math.Max(0, _config.UnresolvedPoolGraceMinutes));
            return new PairLookup(null, transient);
        }

        private async Task<PoolSnapshot> GetSnapshotAsync(TimeSpan? forceIfOlderThan, CancellationToken cancellationToken)
        {
            var maxAge = forceIfOlderThan ?? TimeSpan.FromSeconds(Math.Max(1, _config.PoolSnapshotSeconds));
            var snapshot = _snapshot;
            if (_time.GetUtcNow() - snapshot.LoadedAt < maxAge) return snapshot;

            await _snapshotLock.WaitAsync(cancellationToken);
            try
            {
                snapshot = _snapshot;
                if (_time.GetUtcNow() - snapshot.LoadedAt < maxAge) return snapshot;

                var pairs = new Dictionary<ulong, PairInfo>();
                var unresolved = new Dictionary<ulong, DateTimeOffset>();
                var assetIds = new HashSet<ulong>();
                var now = _time.GetUtcNow();
                foreach (var protocol in _config.Protocols)
                {
                    var pools = await _poolRepository.GetPoolsAsync(null, null, null, protocol, int.MaxValue, null, SortDirection.Desc, cancellationToken);
                    foreach (var pool in pools)
                    {
                        if (!IsPublishable(pool)) continue;
                        assetIds.Add(pool.AssetIdA!.Value);
                        assetIds.Add(pool.AssetIdB!.Value);
                        var pair = await ToPairInfoAsync(pool, cancellationToken);
                        if (pair != null) pairs[pair.AppId] = pair;
                        else
                        {
                            // keep the time it was first seen undescribable across snapshot refreshes
                            unresolved[pool.PoolAppId] = snapshot.Unresolved.TryGetValue(pool.PoolAppId, out var first) ? first : now;
                            _logger.LogWarning("CoinGecko: pool {appId} cannot be described (asset decimals unknown), unresolved since {since}", pool.PoolAppId, unresolved[pool.PoolAppId]);
                        }
                    }
                }
                _snapshot = new PoolSnapshot(pairs, unresolved, assetIds, now);
                return _snapshot;
            }
            finally
            {
                _snapshotLock.Release();
            }
        }

        /// <summary>
        /// A pool GeckoTerminal may see. A permanent "no" (scam, malformed): such pools are skipped silently, unlike a
        /// publishable pool that is only temporarily undescribable (see <see cref="PoolSnapshot.Unresolved"/>).
        /// </summary>
        private static bool IsPublishable(Pool pool)
        {
            // scam pools are relabelled DEXProtocol.Scam (and never listed for a published protocol); belt and braces for stale cache entries
            if (pool.PoolAppId == 0 || pool.AssetIdA == null || pool.AssetIdB == null || pool.ScamRating > 80) return false;
            return pool.AssetIdA != pool.AssetIdB;
        }

        private async Task<PairInfo?> ToPairInfoAsync(Pool pool, CancellationToken cancellationToken)
        {
            int? decimals0, decimals1;
            try
            {
                decimals0 = await ResolveDecimalsAsync(pool.AssetADecimals, pool.AssetIdA!.Value, cancellationToken);
                decimals1 = await ResolveDecimalsAsync(pool.AssetBDecimals, pool.AssetIdB!.Value, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // one failing lookup must not throw away the refresh of every other pool
                _logger.LogWarning(ex, "CoinGecko: could not resolve asset decimals of pool {appId}", pool.PoolAppId);
                return null;
            }
            if (decimals0 == null || decimals1 == null) return null;
            return new PairInfo(pool.PoolAppId, pool.Protocol, pool.AssetIdA.Value, pool.AssetIdB.Value, decimals0.Value, decimals1.Value, pool.LPFee);
        }

        private async Task<int?> ResolveDecimalsAsync(ulong? poolDecimals, ulong assetId, CancellationToken cancellationToken)
        {
            if (poolDecimals.HasValue) return (int)poolDecimals.Value;
            var decimals = (await _assetRepository.GetAssetAsync(assetId, cancellationToken))?.Params?.Decimals;
            return decimals.HasValue ? (int)decimals.Value : null;
        }

        // ---------------------------------------------------------------- events

        public async Task<CoinGeckoResult<byte[]>> GetEventsJsonAsync(ulong fromBlock, ulong toBlock, CancellationToken cancellationToken)
        {
            if (!_config.Enabled) return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.Unavailable, "disabled");
            if (toBlock < fromBlock) return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.BadRequest, "toBlock must be greater than or equal to fromBlock");
            if (toBlock - fromBlock >= (ulong)Math.Max(1, _config.MaxBlockSpan))
                return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.BadRequest, $"At most {_config.MaxBlockSpan} blocks can be requested at once");
            if (!_source.IsAvailable) return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.Unavailable, "Event storage is not available");

            var latest = await _tracker.GetLatestAsync(cancellationToken);
            if (latest == null) return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.Unavailable, "The indexer has not completed a block yet");
            // Beyond latest-block the data is incomplete; answering would let the consumer skip events for good.
            if (toBlock > latest.Round)
                return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.BadRequest, $"toBlock {toBlock} is beyond the latest indexed block {latest.Round}");

            var key = $"{fromBlock}:{toBlock}";
            if (_eventsCache.TryGetValue(key, out byte[]? hit) && hit != null) return CoinGeckoResult<byte[]>.Ok(hit);

            var redisKey = $"{_redisConfig.EnvironmentKeyPrefix}coingecko:events:v1:{key}";
            var fromRedis = await TryReadRedisAsync(redisKey);
            if (fromRedis != null)
            {
                RememberInMemory(key, fromRedis);
                return CoinGeckoResult<byte[]>.Ok(fromRedis);
            }

            // Single flight: identical concurrent requests (several consumers, retries) share one Elasticsearch round trip.
            var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(() => ComputeAndCacheAsync(fromBlock, toBlock, key, redisKey)));
            try
            {
                return CoinGeckoResult<byte[]>.Ok(await lazy.Value.WaitAsync(cancellationToken));
            }
            catch (TooManyEventsException ex)
            {
                return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.BadRequest, ex.Message);
            }
            catch (TransientDataException ex)
            {
                _logger.LogWarning("CoinGecko events {from}-{to} cannot be answered completely right now: {reason}", fromBlock, toBlock, ex.Message);
                return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.Unavailable, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Elasticsearch / Redis / algod trouble: a retryable 503 (the consumer keeps its position), never a partial answer.
                _logger.LogError(ex, "CoinGecko events {from}-{to} failed", fromBlock, toBlock);
                return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.Unavailable, "Events cannot be loaded right now, try again");
            }
        }

        private async Task<byte[]> ComputeAndCacheAsync(ulong fromBlock, ulong toBlock, string key, string redisKey)
        {
            try
            {
                // CancellationToken.None: the shared computation must not be killed by whichever caller cancels first.
                var json = JsonSerializer.SerializeToUtf8Bytes(new CoinGeckoEventsResponse { Events = await BuildEventsAsync(fromBlock, toBlock, CancellationToken.None) }, JsonOptions);
                RememberInMemory(key, json);
                await TryWriteRedisAsync(redisKey, json);
                return json;
            }
            finally
            {
                // Removed by the computation itself (not by a waiting caller, who may have been cancelled): a finished
                // entry must never be reused by a later request - the caches above are the only place results live.
                _inFlight.TryRemove(key, out _);
            }
        }

        internal async Task<List<CoinGeckoEvent>> BuildEventsAsync(ulong fromBlock, ulong toBlock, CancellationToken cancellationToken)
        {
            var budget = new EventBudget(_config.MaxEventsPerRequest);
            var tradesTask = FetchBisectingAsync((lo, hi, size) => _source.GetTradesAsync(lo, hi, size, cancellationToken), fromBlock, toBlock, budget);
            var liquidityTask = FetchBisectingAsync((lo, hi, size) => _source.GetLiquidityAsync(lo, hi, size, cancellationToken), fromBlock, toBlock, budget);
            await Task.WhenAll(tradesTask, liquidityTask);
            var trades = tradesTask.Result.DistinctBy(t => t.TxId).ToList();
            var liquidity = liquidityTask.Result.DistinctBy(l => l.TxId).ToList();

            var keys = new List<EventOrderKey>(trades.Count + liquidity.Count);
            foreach (var t in trades) keys.Add(new EventOrderKey(t.BlockId, t.TxnIndex, t.EventIndex, string.IsNullOrEmpty(t.TopTxId) ? t.TxId : t.TopTxId, t.TxId, 0));
            foreach (var l in liquidity) keys.Add(new EventOrderKey(l.BlockId, l.TxnIndex, l.EventIndex, string.IsNullOrEmpty(l.TopTxId) ? l.TxId : l.TopTxId, l.TxId, 1));
            var positions = CoinGeckoEventOrdering.AssignPositions(keys);

            var events = new List<CoinGeckoEvent>(keys.Count);
            var skipped = 0;
            for (var i = 0; i < keys.Count; i++)
            {
                var isTrade = i < trades.Count;
                var appId = isTrade ? trades[i].PoolAppId : liquidity[i - trades.Count].PoolAppId;
                var lookup = await GetPairInfoAsync(appId, cancellationToken);
                // A published pool that cannot be described right now is not "unknown": skipping its events would be cached
                // as the final answer for an immutable range and GeckoTerminal would never see them.
                if (lookup.Transient) throw new TransientDataException($"pool {appId} cannot be described right now");
                var pair = lookup.Pair;
                CoinGeckoEvent? mapped = pair == null ? null
                    : isTrade ? CoinGeckoMapper.TryMapSwap(trades[i], pair, positions[i])
                    : CoinGeckoMapper.TryMapLiquidity(liquidity[i - trades.Count], pair, positions[i]);
                if (mapped == null)
                {
                    skipped++;
                    continue;
                }
                events.Add(mapped);
            }
            if (skipped > 0)
            {
                _logger.LogWarning("CoinGecko events {from}-{to}: skipped {skipped} of {total} events that cannot be expressed in the GeckoTerminal schema (unknown/scam pool, zero amount, asset mismatch)", fromBlock, toBlock, skipped, keys.Count);
            }

            events.Sort((a, b) =>
            {
                var c = a.Block.BlockNumber.CompareTo(b.Block.BlockNumber);
                if (c != 0) return c;
                c = a.TxnIndex.CompareTo(b.TxnIndex);
                return c != 0 ? c : a.EventIndex.CompareTo(b.EventIndex);
            });
            return events;
        }

        private sealed class EventBudget
        {
            private int _remaining;
            private readonly int _limit;
            public EventBudget(int limit) { _limit = limit; _remaining = limit; }
            public void Consume(int count)
            {
                if (Interlocked.Add(ref _remaining, -count) < 0)
                    throw new TooManyEventsException($"The range contains more than {_limit} events, request fewer blocks");
            }
        }

        private sealed class TooManyEventsException : Exception
        {
            public TooManyEventsException(string message) : base(message) { }
        }

        /// <summary>
        /// Loads every document of the block range without pagination limits: a full page might be truncated, so the
        /// range is halved (down to a single block) until each query returns fewer documents than the page size.
        /// </summary>
        private async Task<List<T>> FetchBisectingAsync<T>(Func<ulong, ulong, int, Task<IReadOnlyList<T>>> query, ulong lo, ulong hi, EventBudget budget)
        {
            var pageSize = Math.Clamp(_config.ElasticPageSize, 1, 10000);
            var singleBlockSize = 10000; // index.max_result_window default; one block never holds this many DEX events
            var size = lo == hi ? singleBlockSize : pageSize;
            var page = await query(lo, hi, size);
            if (page.Count < size)
            {
                budget.Consume(page.Count);
                return page.ToList();
            }
            if (lo == hi) throw new InvalidOperationException($"Block {lo} holds {page.Count} or more events - more than one query can return");

            var mid = lo + (hi - lo) / 2;
            var leftTask = FetchBisectingAsync(query, lo, mid, budget);
            var rightTask = FetchBisectingAsync(query, mid + 1, hi, budget);
            await Task.WhenAll(leftTask, rightTask);
            var left = leftTask.Result;
            left.AddRange(rightTask.Result);
            return left;
        }

        // ---------------------------------------------------------------- caching helpers

        private void RememberInMemory(string key, byte[] json)
        {
            _eventsCache.Set(key, json, new MemoryCacheEntryOptions
            {
                Size = Math.Max(1, json.Length),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, _config.EventsCacheSeconds)),
            });
        }

        private async Task<byte[]?> TryReadRedisAsync(string redisKey)
        {
            if (_redis == null) return null;
            try
            {
                var value = await _redis.StringGetAsync(redisKey);
                if (!value.HasValue) return null;
                await using var input = new MemoryStream((byte[])value!);
                await using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                await gzip.CopyToAsync(output);
                return output.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CoinGecko events cache read failed");
                return null;
            }
        }

        private async Task TryWriteRedisAsync(string redisKey, byte[] json)
        {
            if (_redis == null) return;
            try
            {
                using var output = new MemoryStream();
                await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                {
                    await gzip.WriteAsync(json);
                }
                await _redis.StringSetAsync(redisKey, output.ToArray(), TimeSpan.FromSeconds(Math.Max(1, _config.EventsCacheSeconds)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CoinGecko events cache write failed");
            }
        }

        private static bool TryParseId(string? id, out ulong value)
        {
            value = 0;
            return !string.IsNullOrWhiteSpace(id)
                && id.All(char.IsAsciiDigit)
                && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
    }
}
