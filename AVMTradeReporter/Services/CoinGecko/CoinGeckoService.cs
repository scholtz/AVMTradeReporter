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

        /// <summary>Builds the pair snapshot once at startup, before the pod serves traffic (CLAUDE.md "HA deploys").</summary>
        Task WarmUpAsync(CancellationToken cancellationToken);
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
        private readonly object _rebuildLock = new();
        private Task<PoolSnapshot>? _rebuild;
        private readonly string _protocolFingerprint;
        private readonly SemaphoreSlim _computeGate;
        private volatile PoolSnapshot _snapshot = new(new Dictionary<ulong, PairInfo>(), new Dictionary<ulong, DateTimeOffset>(), new HashSet<ulong>(), new HashSet<ulong>(), DateTimeOffset.MinValue, DateTimeOffset.MinValue, true);

        /// <param name="Pairs">Published pairs by pool application id.</param>
        /// <param name="Unresolved">Published pools whose asset decimals could not be read right now (algod), with the time they were first seen so. Always transient: the assets of a live pool exist; only a destroyed-asset tombstone is permanent and lands in <paramref name="Excluded"/>.</param>
        /// <param name="AssetIds">Every asset of a published pool: the only assets <c>/asset</c> answers for.</param>
        /// <param name="Excluded">Pools the repository knows but that must never be published (scam, malformed).</param>
        /// <param name="LoadedAt">When this snapshot was built (a forced, in-memory rebuild counts).</param>
        /// <param name="FullRefreshAt">When unresolved pools were last retried at the asset repository - the regular cadence, which forced rebuilds must not keep postponing.</param>
        /// <param name="PoolCacheEmpty">The pool repository held no pool at all (any protocol) AND never finished loading: not initialised on this pod, or still warming. A repository that loaded fine and simply has no pool (a network the DEX is not on yet) is NOT this - its lookups are honest 404s and its events an empty list.</param>
        private sealed record PoolSnapshot(Dictionary<ulong, PairInfo> Pairs, Dictionary<ulong, DateTimeOffset> Unresolved, HashSet<ulong> AssetIds, HashSet<ulong> Excluded, DateTimeOffset LoadedAt, DateTimeOffset FullRefreshAt, bool PoolCacheEmpty);

        /// <summary>Pair lookup result: <see cref="Transient"/> means "exists but cannot be described right now - try again", not "unknown".</summary>
        /// <param name="Excluded">The pool is known but deliberately not published, so its events are skipped for good.</param>
        private readonly record struct PairLookup(PairInfo? Pair, bool Transient, bool Excluded = false);

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
            _computeGate = new SemaphoreSlim(Math.Max(1, _config.MaxConcurrentEventQueries));
            // part of every events cache key: a changed protocol list must not be served from the previous configuration's cache
            _protocolFingerprint = string.Join("+", _config.PublishedProtocols.Select(p => p.ToString().ToLowerInvariant()).OrderBy(p => p, StringComparer.Ordinal));
            _eventsCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = Math.Max(1, _config.EventsMemoryCacheMegabytes) * 1024L * 1024L });
        }

        // ---------------------------------------------------------------- latest block

        public async Task<CoinGeckoResult<CoinGeckoLatestBlockResponse>> GetLatestBlockAsync(CancellationToken cancellationToken)
        {
            if (!_config.Enabled) return CoinGeckoResult<CoinGeckoLatestBlockResponse>.Fail(CoinGeckoOutcome.Unavailable, "disabled");
            // Without event storage the watermark means nothing to a consumer: /events cannot be answered either.
            if (!_source.IsAvailable) return CoinGeckoResult<CoinGeckoLatestBlockResponse>.Fail(CoinGeckoOutcome.Unavailable, "Event storage is not available");
            var latest = await _tracker.GetLatestAsync(cancellationToken);
            if (latest == null) return CoinGeckoResult<CoinGeckoLatestBlockResponse>.Fail(CoinGeckoOutcome.Unavailable, "The indexer has not completed a block yet");
            return CoinGeckoResult<CoinGeckoLatestBlockResponse>.Ok(new CoinGeckoLatestBlockResponse
            {
                Block = new CoinGeckoBlock { BlockNumber = latest.Round, BlockTimestamp = latest.UnixTimestamp },
            });
        }

        public async Task WarmUpAsync(CancellationToken cancellationToken)
        {
            if (!_config.Enabled) return;
            try
            {
                await GetSnapshotAsync(forceIfOlderThan: null, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "CoinGecko: pair snapshot warm-up failed - the integration answers 503 until a refresh succeeds");
            }
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
            if (!snapshot.AssetIds.Contains(assetId))
            {
                // same rule as /pair: with no pool cache at all nothing can be said about any asset yet
                if (snapshot.PoolCacheEmpty) return CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.Unavailable, "The pool cache is not available right now, try again");
                return CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.NotFound, "Asset not found");
            }

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
            if (stored == null && !await _assetRepository.IsDeletedAsync(assetId, cancellationToken))
            {
                // null means "could not be read right now" (algod timeout) unless the repository holds a destroyed-asset tombstone:
                // a published asset that exists is a retryable 503, never a cached 404 the indexer would act on.
                return CoinGeckoResult<CoinGeckoAssetResponse>.Fail(CoinGeckoOutcome.Unavailable, "The asset cannot be loaded right now, try again");
            }
            var asset = MapAsset(assetId, stored);
            // a destroyed asset of a published pool is cached briefly so repeated lookups do not hit algod each time
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

            // The native token is synthesized by AssetRepository with its supply in WHOLE units (10 billion) next to
            // Decimals = 6, unlike an ASA whose Total is in base units - decimalizing it would be 1e6 too small.
            var total = !parameters.Total.HasValue ? null
                : assetId == 0 ? new decimal(parameters.Total.Value)
                : CoinGeckoMapper.Decimalize(parameters.Total.Value, decimals);
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
            // Known and deliberately not published (scam, malformed): no point in rebuilding the snapshot to look for it again.
            if (snapshot.Excluded.Contains(appId)) return new PairLookup(null, false, true);

            // A pool created after the snapshot was taken: refresh once (rate limited, in-memory only) before answering "unknown".
            snapshot = await GetSnapshotAsync(MissingPoolRefreshInterval, cancellationToken);
            if (snapshot.Pairs.TryGetValue(appId, out pair)) return new PairLookup(pair, false);
            // An empty pool cache that never finished loading (PoolRepository.InitializeAsync failed on this pod, or still warming) makes every pool look
            // unknown and would cache every range as "no events": transient, never final. A deployment that simply has no pool
            // of a published protocol is a different thing - its lookups are honest 404s.
            if (snapshot.PoolCacheEmpty) return new PairLookup(null, true);
            // An undescribable pool (asset decimals not readable right now) stays transient until it resolves: its events exist
            // and must not be cached away. The permanent case - a destroyed asset - is a tombstone and is in Excluded instead.
            // "Known" = the pool cache has the pool; only a pool the cache has never heard of can be a brand new one.
            return new PairLookup(null, snapshot.Unresolved.ContainsKey(appId), false); // excluded pools returned above, before the refresh
        }

        private async Task<PoolSnapshot> GetSnapshotAsync(TimeSpan? forceIfOlderThan, CancellationToken cancellationToken)
        {
            var regular = TimeSpan.FromSeconds(Math.Max(1, _config.PoolSnapshotSeconds));
            var maxAge = forceIfOlderThan ?? regular;
            PoolSnapshot snapshot;
            var now = _time.GetUtcNow();
            Task<PoolSnapshot>? rebuild;
            lock (_rebuildLock)
            {
                snapshot = _snapshot; // read under the lock: a rebuild that just finished must not trigger another one
                rebuild = _rebuild is { IsCompleted: false } running ? running : null;
                if (rebuild == null && now - snapshot.LoadedAt >= maxAge)
                {
                    // Single flight. A forced rebuild (somebody asked for an id the snapshot lacks - possibly an anonymous
                    // caller probing random ids) only re-reads the in-memory pool cache; the asset-repository lookups behind
                    // unresolved pools follow the regular cadence (FullRefreshAt), which forced rebuilds must not postpone.
                    var retryUnresolved = now - snapshot.FullRefreshAt >= regular;
                    rebuild = _rebuild = Task.Run(() => RebuildSnapshotAsync(snapshot, retryUnresolved));
                    _ = rebuild.ContinueWith(t => _logger.LogError(t.Exception, "CoinGecko: pair snapshot rebuild failed"), TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            if (rebuild == null) return snapshot;
            // Stale-while-revalidate: whatever can be answered from the previous snapshot is, so a slow asset lookup inside the
            // rebuild never stalls /pair, /asset or /events. The very first build (nothing to serve yet) and a lookup of an id
            // the snapshot lacks (maybe a pool created a moment ago) wait for the fresh one.
            if (forceIfOlderThan == null && snapshot.LoadedAt != DateTimeOffset.MinValue) return snapshot;
            return await rebuild.WaitAsync(cancellationToken);
        }

        private async Task<PoolSnapshot> RebuildSnapshotAsync(PoolSnapshot snapshot, bool retryUnresolved)
        {
            // A pod whose pool load was never answered (Redis / Elasticsearch down at start) recovers here: InitializeAsync reloads
            // (throttled inside the repository) while the snapshot keeps being rebuilt, instead of waiting for the hourly refresh.
            if (!_poolRepository.PoolLoadSucceeded)
            {
                try { await _poolRepository.InitializeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "CoinGecko: retrying the pool cache load failed"); }
            }
            var pairs = new Dictionary<ulong, PairInfo>();
            var unresolved = new Dictionary<ulong, DateTimeOffset>();
            var assetIds = new HashSet<ulong>();
            var excluded = new HashSet<ulong>();
            var now = _time.GetUtcNow();
            var published = _config.PublishedProtocols;
            var toResolve = new List<Pool>();
            var anyPool = false;

            // One pass over the raw pool cache (no sort, no enrichment - GetPoolsAsync would do both, and a forced refresh may
            // run every 5 s under probing). Scam-labelled pools are classified too: their stored swaps still carry the
            // protocol they had before the relabel, and "known but excluded" must not be confused with "not registered yet".
            foreach (var address in _poolRepository.GetAllPoolAddresses())
            {
                // the RAW entry: GetPoolAsync hides pools whose decimals are not enriched yet, which are exactly the ones
                // that must land in Unresolved (held back) instead of looking like pools the cache never heard of
                var pool = _poolRepository.GetCachedPool(address);
                if (pool == null) continue;
                anyPool = true;
                if (!published.Contains(pool.Protocol) && pool.Protocol != DEXProtocol.Scam) continue;
                if (!published.Contains(pool.Protocol) || !IsPublishable(pool))
                {
                    excluded.Add(pool.PoolAppId);
                    continue;
                }
                assetIds.Add(pool.AssetIdA!.Value);
                assetIds.Add(pool.AssetIdB!.Value);
                if (snapshot.Pairs.TryGetValue(pool.PoolAppId, out var known))
                {
                    // decimals never change; only the fee is re-read
                    pairs[pool.PoolAppId] = known with { LpFee = pool.LPFee };
                    continue;
                }
                var decimalsOnPool = pool.AssetADecimals.HasValue && pool.AssetBDecimals.HasValue;
                if (snapshot.Excluded.Contains(pool.PoolAppId) && !decimalsOnPool && !retryUnresolved)
                {
                    excluded.Add(pool.PoolAppId); // destroyed asset found earlier: stays excluded between regular refreshes
                    continue;
                }
                if (!retryUnresolved && !decimalsOnPool)
                {
                    // would need the asset repository (algod): on the regular cadence only, never on a probe-triggered rebuild
                    unresolved[pool.PoolAppId] = snapshot.Unresolved.TryGetValue(pool.PoolAppId, out var seenAt) ? seenAt : now;
                    continue;
                }
                toResolve.Add(pool);
            }

            // Asset decimals missing on the pool are read from the asset repository (algod on a cold cache): in parallel,
            // because this also runs as the startup warm-up before the pod opens its port.
            var sync = new object();
            await Parallel.ForEachAsync(toResolve,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _config.SnapshotResolveParallelism) },
                async (pool, ct) =>
                {
                    var (pair, permanent) = await ToPairInfoAsync(pool, ct);
                    lock (sync)
                    {
                        if (pair != null) pairs[pair.AppId] = pair;
                        else if (permanent)
                        {
                            excluded.Add(pool.PoolAppId);
                            if (!snapshot.Excluded.Contains(pool.PoolAppId))
                                _logger.LogWarning("CoinGecko: pool {appId} references a destroyed asset - excluded for good", pool.PoolAppId);
                        }
                        else if (snapshot.Unresolved.TryGetValue(pool.PoolAppId, out var first))
                        {
                            unresolved[pool.PoolAppId] = first; // keep the time it was first seen undescribable
                        }
                        else
                        {
                            unresolved[pool.PoolAppId] = now;
                            _logger.LogWarning("CoinGecko: pool {appId} cannot be described (asset decimals not readable); retried every {seconds} s, its events are held back meanwhile", pool.PoolAppId, _config.PoolSnapshotSeconds);
                        }
                    }
                });
            _snapshot = new PoolSnapshot(pairs, unresolved, assetIds, excluded, now, retryUnresolved ? now : snapshot.FullRefreshAt, !anyPool && !_poolRepository.PoolLoadSucceeded);
            return _snapshot;
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

        /// <returns>The pair, or (null, permanent): permanent when an asset of the pool is destroyed, transient otherwise.</returns>
        private async Task<(PairInfo? Pair, bool Permanent)> ToPairInfoAsync(Pool pool, CancellationToken cancellationToken)
        {
            try
            {
                var a = ResolveDecimalsAsync(pool.AssetADecimals, pool.AssetIdA!.Value, cancellationToken);
                var b = ResolveDecimalsAsync(pool.AssetBDecimals, pool.AssetIdB!.Value, cancellationToken);
                await Task.WhenAll(a, b);
                var (decimals0, permanent0) = a.Result;
                var (decimals1, permanent1) = b.Result;
                if (decimals0 == null || decimals1 == null) return (null, permanent0 || permanent1);
                return (new PairInfo(pool.PoolAppId, pool.Protocol, pool.AssetIdA.Value, pool.AssetIdB.Value, decimals0.Value, decimals1.Value, pool.LPFee), false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // one failing lookup must not throw away the refresh of every other pool
                _logger.LogWarning(ex, "CoinGecko: could not resolve asset decimals of pool {appId}", pool.PoolAppId);
                return (null, false);
            }
        }

        private async Task<(int? Decimals, bool Permanent)> ResolveDecimalsAsync(ulong? poolDecimals, ulong assetId, CancellationToken cancellationToken)
        {
            if (poolDecimals.HasValue) return ((int)poolDecimals.Value, false);
            var decimals = (await _assetRepository.GetAssetAsync(assetId, cancellationToken))?.Params?.Decimals;
            if (decimals.HasValue) return ((int)decimals.Value, false);
            return (null, await _assetRepository.IsDeletedAsync(assetId, cancellationToken));
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
            // Beyond latest-block the data is incomplete; answering would let the consumer skip events for good. Replicas may
            // legitimately disagree by a block (the watermark is mirrored through Redis), so this is a retryable 503, not a 400.
            if (toBlock > latest.Round)
                return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.Unavailable, $"toBlock {toBlock} is beyond the latest indexed block {latest.Round}, try again");

            var key = EventsKey(fromBlock, toBlock);
            if (_eventsCache.TryGetValue(key, out byte[]? hit) && hit != null) return CoinGeckoResult<byte[]>.Ok(hit);
            // a transient failure a moment ago still holds: the consumer retries every ~2 s, the storage queries need not
            if (_eventsCache.TryGetValue("transient:" + key, out string? recentReason) && recentReason != null)
                return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.Unavailable, recentReason);

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
            catch (BlockTooDenseException ex)
            {
                // deterministic: retrying can never help, and a 503 would keep the consumer at this range for ever
                _logger.LogError("CoinGecko events {from}-{to}: {reason}", fromBlock, toBlock, ex.Message);
                return CoinGeckoResult<byte[]>.Fail(CoinGeckoOutcome.BadRequest, ex.Message);
            }
            catch (TransientDataException ex)
            {
                _logger.LogWarning("CoinGecko events {from}-{to} cannot be answered completely right now: {reason}", fromBlock, toBlock, ex.Message);
                if (_config.TransientMemoSeconds > 0)
                    _eventsCache.Set("transient:" + key, ex.Message, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_config.TransientMemoSeconds) });
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
            var tooMany = _eventsCache.TryGetValue("too-many:" + EventsKey(fromBlock, toBlock), out string? knownTooMany) ? knownTooMany : null;
            if (tooMany != null) throw new TooManyEventsException(tooMany);
            using var budget = new EventBudget(_config.MaxEventsPerRequest, _config.MaxParallelQueriesPerRange);
            // At most MaxConcurrentEventQueries uncached ranges query storage at a time: distinct ranges are the one thing a
            // caller can multiply, and each costs Elasticsearch queries. Waiting longer than a few seconds is a retryable 503.
            // Only the storage queries hold a slot - the pair lookups below may wait for a snapshot rebuild (algod), which
            // must not block other ranges' queries.
            if (!await _computeGate.WaitAsync(TimeSpan.FromSeconds(5))) throw new TransientDataException("too many events requests are being built right now");
            List<Trade> trades;
            List<Liquidity> liquidity;
            try
            {
                var tradesTask = FetchBisectingAsync((lo, hi, size) => _source.GetTradesAsync(lo, hi, size, cancellationToken), fromBlock, toBlock, budget);
                var liquidityTask = FetchBisectingAsync((lo, hi, size) => _source.GetLiquidityAsync(lo, hi, size, cancellationToken), fromBlock, toBlock, budget);
                try
                {
                    await Task.WhenAll(tradesTask, liquidityTask);
                }
                catch (TooManyEventsException ex)
                {
                    // remembered briefly so a client repeating the same oversize request does not re-run the whole fan-out
                    _eventsCache.Set("too-many:" + EventsKey(fromBlock, toBlock), ex.Message, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) });
                    throw;
                }
                trades = tradesTask.Result.DistinctBy(t => t.TxId).ToList();
                liquidity = liquidityTask.Result.DistinctBy(l => l.TxId).ToList();
            }
            finally
            {
                _computeGate.Release();
            }

            var keys = new List<EventOrderKey>(trades.Count + liquidity.Count);
            foreach (var t in trades) keys.Add(new EventOrderKey(t.BlockId, t.TxnIndex, t.EventIndex, string.IsNullOrEmpty(t.TopTxId) ? t.TxId : t.TopTxId, t.TxId, 0));
            foreach (var l in liquidity) keys.Add(new EventOrderKey(l.BlockId, l.TxnIndex, l.EventIndex, string.IsNullOrEmpty(l.TopTxId) ? l.TxId : l.TopTxId, l.TxId, 1));
            var positions = CoinGeckoEventOrdering.AssignPositions(keys);

            var events = new List<CoinGeckoEvent>(keys.Count);
            var skipped = 0;
            var lookups = new Dictionary<ulong, PairLookup>(); // each pool is resolved once per request, not once per event
            for (var i = 0; i < keys.Count; i++)
            {
                var isTrade = i < trades.Count;
                var appId = isTrade ? trades[i].PoolAppId : liquidity[i - trades.Count].PoolAppId;
                if (!lookups.TryGetValue(appId, out var lookup))
                {
                    lookup = await GetPairInfoAsync(appId, cancellationToken);
                    lookups[appId] = lookup;
                }
                // A published pool that cannot be described right now is not "unknown": skipping its events would be cached
                // as the final answer for an immutable range and GeckoTerminal would never see them.
                if (lookup.Transient) throw new TransientDataException($"pool {appId} cannot be described right now");
                var pair = lookup.Pair;
                if (pair == null && !lookup.Excluded)
                {
                    // Not known at all: a pool created moments ago that the pool cache has not registered yet looks exactly
                    // like this. Skipping its first events would be cached as final, so recent events wait (503) a little.
                    var eventTime = isTrade ? trades[i].Timestamp : liquidity[i - trades.Count].Timestamp;
                    if (eventTime != null && _time.GetUtcNow() - eventTime.Value < TimeSpan.FromSeconds(Math.Max(0, _config.UnknownPoolGraceSeconds)))
                        throw new TransientDataException($"pool {appId} is not registered yet");
                    // Older and still unknown: the pool cache missed it (its registration threw at the time). The event documents
                    // were written by our own processors, so the pool is real - load it from chain and register it.
                    var (address, protocol) = isTrade ? (trades[i].PoolAddress, trades[i].Protocol) : (liquidity[i - trades.Count].PoolAddress, liquidity[i - trades.Count].Protocol);
                    if (await TryRegisterUnknownPoolAsync(appId, address, protocol, cancellationToken))
                    {
                        await GetSnapshotAsync(TimeSpan.Zero, cancellationToken);
                        lookup = await GetPairInfoAsync(appId, cancellationToken);
                        lookups[appId] = lookup;
                        // still unknown: a rebuild that was already running read the pool list before the registration -
                        // the next request sees it; skipping now would cache the range without the pool's events
                        if (lookup.Transient || (lookup.Pair == null && !lookup.Excluded)) throw new TransientDataException($"pool {appId} was just registered - the pair snapshot is catching up");
                        pair = lookup.Pair;
                    }
                }
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

        /// <summary>
        /// Loads a pool that has stored events but is missing from the pool cache from chain and registers it. True once it
        /// is registered; throws <see cref="TransientDataException"/> (503) while attempts remain - at most one attempt per
        /// <see cref="CoinGeckoConfiguration.PoolSnapshotSeconds"/>; false once <see cref="CoinGeckoConfiguration.UnknownPoolLoadAttempts"/>
        /// are used up, after which the pool's events are skipped.
        /// </summary>
        private async Task<bool> TryRegisterUnknownPoolAsync(ulong appId, string address, DEXProtocol protocol, CancellationToken cancellationToken)
        {
            var attemptsKey = "unknown-pool-attempts:" + appId;
            var attempts = _eventsCache.TryGetValue(attemptsKey, out int made) ? made : 0;
            if (attempts >= Math.Max(0, _config.UnknownPoolLoadAttempts)) return false;
            var triedKey = "unknown-pool-tried:" + appId;
            if (_eventsCache.TryGetValue(triedKey, out _)) throw new TransientDataException($"pool {appId} is not registered yet - a load from chain was attempted recently");
            _eventsCache.Set(triedKey, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, _config.PoolSnapshotSeconds)) });
            _eventsCache.Set(attemptsKey, attempts + 1, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6) });
            try
            {
                var processor = _poolRepository.GetPoolProcessor(protocol);
                var pool = processor == null || string.IsNullOrEmpty(address) ? null : await processor.LoadPoolAsync(address, appId);
                if (pool != null && await _poolRepository.StorePoolAsync(pool, true, cancellationToken))
                {
                    _logger.LogWarning("CoinGecko: pool {appId} had stored events but was missing from the pool cache - registered it from chain", appId);
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "CoinGecko: loading pool {appId} from chain failed (attempt {attempt} of {max})", appId, attempts + 1, _config.UnknownPoolLoadAttempts);
            }
            throw new TransientDataException($"pool {appId} is not registered and could not be loaded from chain (attempt {attempts + 1} of {_config.UnknownPoolLoadAttempts})");
        }

        private sealed class EventBudget : IDisposable
        {
            private int _remaining;
            private readonly int _limit;

            /// <summary>Caps the Elasticsearch queries one range runs at once: bisecting a dense range doubles the fan-out per level.</summary>
            public SemaphoreSlim Gate { get; }

            public EventBudget(int limit, int maxParallelQueries)
            {
                _limit = limit;
                _remaining = limit;
                Gate = new SemaphoreSlim(Math.Max(1, maxParallelQueries));
            }

            public void Dispose() => Gate.Dispose();
            public void Consume(int count)
            {
                if (Interlocked.Add(ref _remaining, -count) < 0) ThrowIfExceeded();
            }

            /// <summary>Checked before every query so a range that is already too big stops spawning more queries.</summary>
            public void ThrowIfExceeded()
            {
                if (Volatile.Read(ref _remaining) < 0)
                    throw new TooManyEventsException($"The range contains more than {_limit} events, request fewer blocks");
            }
        }

        private sealed class TooManyEventsException : Exception
        {
            public TooManyEventsException(string message) : base(message) { }
        }

        /// <summary>One block holds more events than a single Elasticsearch query can return (index.max_result_window).</summary>
        private sealed class BlockTooDenseException : Exception
        {
            public BlockTooDenseException(string message) : base(message) { }
        }

        private string EventsKey(ulong fromBlock, ulong toBlock) => $"{_protocolFingerprint}:{fromBlock}:{toBlock}";

        /// <summary>
        /// Loads every document of the block range without pagination limits: a full page might be truncated, so the
        /// range is halved (down to a single block) until each query returns fewer documents than the page size.
        /// </summary>
        private async Task<List<T>> FetchBisectingAsync<T>(Func<ulong, ulong, int, Task<IReadOnlyList<T>>> query, ulong lo, ulong hi, EventBudget budget)
        {
            budget.ThrowIfExceeded();
            var pageSize = Math.Clamp(_config.ElasticPageSize, 1, 10000);
            var singleBlockSize = 10000; // index.max_result_window default; one block never holds this many DEX events
            var size = lo == hi ? singleBlockSize : pageSize;
            IReadOnlyList<T> page;
            await budget.Gate.WaitAsync();
            try
            {
                page = await query(lo, hi, size);
            }
            finally
            {
                budget.Gate.Release();
            }
            if (page.Count < size)
            {
                budget.Consume(page.Count);
                return page.ToList();
            }
            if (lo == hi) throw new BlockTooDenseException($"Block {lo} holds {page.Count} or more events - more than this API can return for one block");

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
            // NumberStyles.None: digits only - no sign, whitespace, separators or exponent
            return !string.IsNullOrWhiteSpace(id) && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
    }
}
