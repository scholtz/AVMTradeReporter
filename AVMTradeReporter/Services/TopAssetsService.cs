using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Computes the "top assets" highlight lists (Popular, Trending, Top gainers/losers,
    /// Top value gainers/losers) from the in-memory asset cache and caches the result in Redis so the
    /// endpoint is served mostly from cache under heavy traffic.
    ///
    /// The 24h real-TVL change is diffed against the durable Elasticsearch "tvlohlc" index (see
    /// <see cref="TvlOhlcRepository.GetTvlAtOrBeforeAsync"/>), batched in one query for every
    /// candidate asset. An asset with no TVL-OHLC doc at/near 24h ago is treated as having had zero
    /// TVL then (same semantics as the old Redis-hourly-snapshot approach this replaced).
    /// </summary>
    public class TopAssetsService : ITopAssetsService
    {
        private const string CacheKeyBase = "asset:top:summary";
        private string CacheKey => _appConfig.Redis.EnvironmentKeyPrefix + CacheKeyBase;

        private readonly IAssetRepository _assetRepository;
        private readonly ILogger<TopAssetsService> _logger;
        private readonly IDatabase? _redisDatabase;
        private readonly AppConfiguration _appConfig;
        private readonly ITradeQueryService? _tradeQueryService;
        private readonly TvlOhlcRepository? _tvlOhlcRepository;

        // Fallback served when Redis is unavailable/empty and a fresh computation fails or is redundant.
        private TopAssetsResponse? _lastComputed;

        public TopAssetsService(
            IAssetRepository assetRepository,
            ILogger<TopAssetsService> logger,
            IOptions<AppConfiguration> appConfig,
            IDatabase? redisDatabase = null,
            ITradeQueryService? tradeQueryService = null,
            TvlOhlcRepository? tvlOhlcRepository = null)
        {
            _assetRepository = assetRepository;
            _logger = logger;
            _appConfig = appConfig.Value;
            _redisDatabase = redisDatabase;
            _tradeQueryService = tradeQueryService;
            _tvlOhlcRepository = tvlOhlcRepository;
        }

        public async Task<TopAssetsResponse> GetTopAssetsAsync(CancellationToken cancellationToken = default)
        {
            if (_redisDatabase != null)
            {
                try
                {
                    var cached = await _redisDatabase.StringGetAsync(CacheKey);
                    if (cached.HasValue)
                    {
                        var response = JsonSerializer.Deserialize<TopAssetsResponse>((string)cached!);
                        if (response != null)
                        {
                            _lastComputed = response;
                            return response;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read top assets from Redis cache, falling back to in-memory value");
                }
            }

            if (_lastComputed != null) return _lastComputed;

            // Cold start (first request before the background service populated the cache).
            return await ComputeAndCacheAsync(cancellationToken);
        }

        public async Task<TopAssetsResponse> ComputeAndCacheAsync(CancellationToken cancellationToken = default)
        {
            var universeSize = Math.Max(1, _appConfig.TopAssets?.UniverseSize ?? 150);
            var listSize = Math.Max(1, _appConfig.TopAssets?.ListSize ?? 3);
            var now = DateTimeOffset.UtcNow;

            // GetAssetsAsync already orders by real TVL (TVL_USD) descending.
            var universe = (await _assetRepository.GetAssetsAsync(null, null, 0, universeSize, cancellationToken)).ToList();

            var tvl24hAgo = await LoadTvl24hAgoAsync(universe, now, cancellationToken);

            // Windowed volumes straight from confirmed trades. The Volume1H/Volume24H counters on
            // BiatecAsset only re-window during pool refresh and go stale in between (they never
            // decay without new trades), which made "trending" list assets with no trades in the
            // past hour. Null (ES unavailable) falls back to the asset counters inside Build.
            IReadOnlyDictionary<ulong, AssetVolumeWindows>? assetVolumes = null;
            if (_tradeQueryService != null)
            {
                assetVolumes = await _tradeQueryService.GetAssetVolumeWindowsAsync(now, cancellationToken);
            }

            await SyncAssetVolumeCountersAsync(universe, assetVolumes, cancellationToken);

            var minRealTvlUsd = _appConfig.TopAssets?.MinRealTvlUsd ?? 1000m;
            var response = Build(universe, tvl24hAgo, assetVolumes, listSize, now, minRealTvlUsd);
            _lastComputed = response;

            if (_redisDatabase != null)
            {
                try
                {
                    var intervalSeconds = Math.Max(60, _appConfig.TopAssets?.IntervalSeconds ?? 300);
                    // TTL is 3x the refresh interval so a temporarily failing recompute does not empty the cache.
                    await _redisDatabase.StringSetAsync(CacheKey, JsonSerializer.Serialize(response), TimeSpan.FromSeconds(intervalSeconds * 3));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to store top assets in Redis cache");
                }
            }

            return response;
        }

        /// <summary>
        /// Pure selection logic: builds the highlight lists from the TVL-ordered candidate universe.
        /// Stable/base reference assets (StabilityIndex &gt; 0, e.g. ALGO and USDC) are excluded from
        /// the volume-ranked lists (Popular, Trending) — as pricing anchors they'd permanently occupy
        /// those — but compete normally in the price and liquidity gainers/losers lists.
        /// The "positive" lists (Popular, Trending, Top gainers, Top liquidity gainers) additionally
        /// require a real TVL above <paramref name="minRealTvlUsd"/> so dust-liquidity tokens cannot
        /// occupy them; the loser lists deliberately have no such floor — an asset draining to (or
        /// trading at) near-zero is exactly what they should surface.
        /// </summary>
        public static TopAssetsResponse Build(
            IEnumerable<BiatecAsset> universeOrderedByTvl,
            IReadOnlyDictionary<ulong, decimal>? tvl24hAgo,
            IReadOnlyDictionary<ulong, AssetVolumeWindows>? assetVolumes,
            int listSize,
            DateTimeOffset now,
            decimal minRealTvlUsd = 0m)
        {
            var universe = universeOrderedByTvl
                .Select(a => (a.StabilityIndex, Item: ToItem(a, tvl24hAgo, assetVolumes)))
                .ToList();
            var candidates = universe.Select(x => x.Item).ToList();
            var liquid = candidates.Where(i => i.RealTVLUSD > minRealTvlUsd).ToList();
            var liquidNonStable = universe
                .Where(x => x.StabilityIndex == 0 && x.Item.RealTVLUSD > minRealTvlUsd)
                .Select(x => x.Item)
                .ToList();

            return new TopAssetsResponse
            {
                Popular = liquidNonStable
                    .Where(i => i.Volume24HUSD > 0)
                    .OrderByDescending(i => i.Volume24HUSD)
                    .Take(listSize).ToList(),
                Trending = liquidNonStable
                    .Where(i => i.Volume1HUSD > 0)
                    .OrderByDescending(i => i.Volume1HUSD)
                    .Take(listSize).ToList(),
                TopGainers = liquid
                    .Where(i => i.PriceChange24HPercent > 0)
                    .OrderByDescending(i => i.PriceChange24HPercent)
                    .Take(listSize).ToList(),
                TopLosers = candidates
                    .Where(i => i.PriceChange24HPercent < 0)
                    .OrderBy(i => i.PriceChange24HPercent)
                    .Take(listSize).ToList(),
                // Newly funded assets (absent from the 24h-ago snapshot) have an undefined
                // (null) percent change from a zero baseline, so they're excluded here rather
                // than ranked as "infinite" growth ahead of assets with a real, finite gain.
                TopValueGainers = liquid
                    .Where(i => i.TVLChange24HUSD > 0 && i.TVLChange24HPercent != null)
                    .OrderByDescending(i => i.TVLChange24HPercent)
                    .ThenByDescending(i => i.TVLChange24HUSD)
                    .Take(listSize).ToList(),
                TopValueLosers = candidates
                    .Where(i => i.TVLChange24HPercent < 0)
                    .OrderBy(i => i.TVLChange24HPercent)
                    .ThenBy(i => i.TVLChange24HUSD)
                    .Take(listSize).ToList(),
                GeneratedAt = now
            };
        }

        /// <summary>
        /// Writes the honest windowed 1h/24h volumes back onto the asset records so the assets
        /// table (served from BiatecAsset.Volume1H/Volume24H) shows the same numbers as the
        /// Popular/Trending highlight cards. Without this the two sources drift: the asset counters
        /// are only re-summed from pool volumes when a pool has recent trades, so they never decay
        /// to zero for assets that stopped trading. Assets absent from <paramref name="assetVolumes"/>
        /// had no trades in the past 48h and get their counters zeroed.
        /// </summary>
        private async Task SyncAssetVolumeCountersAsync(
            IReadOnlyCollection<BiatecAsset> universe,
            IReadOnlyDictionary<ulong, AssetVolumeWindows>? assetVolumes,
            CancellationToken cancellationToken)
        {
            if (assetVolumes == null) return; // trade history unavailable — keep existing counters
            foreach (var asset in universe)
            {
                cancellationToken.ThrowIfCancellationRequested();
                assetVolumes.TryGetValue(asset.Index, out var windows);
                var volume1H = windows?.Volume1H ?? 0m;
                var volume24H = windows?.Volume24H ?? 0m;
                // Cent-level tolerance: skip write-back churn (Redis writes + SignalR broadcasts)
                // when the counters are already in sync.
                if (Math.Abs((asset.Volume1H ?? 0m) - volume1H) < 0.01m
                    && Math.Abs((asset.Volume24H ?? 0m) - volume24H) < 0.01m)
                {
                    continue;
                }
                asset.Volume1H = volume1H;
                asset.Volume24H = volume24H;
                try
                {
                    await _assetRepository.SetAssetAsync(asset, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync windowed volumes onto asset {AssetId}", asset.Index);
                }
            }
        }

        private static TopAssetItem ToItem(
            BiatecAsset asset,
            IReadOnlyDictionary<ulong, decimal>? tvl24hAgo,
            IReadOnlyDictionary<ulong, AssetVolumeWindows>? assetVolumes)
        {
            var item = new TopAssetItem
            {
                AssetId = asset.Index,
                Name = asset.Params?.Name,
                UnitName = asset.Params?.UnitName,
                Decimals = asset.Params?.Decimals,
                PriceUSD = asset.PriceUSD,
                PriceUSD24H = asset.PriceUSD24H,
                // Fallback to the (possibly stale) asset counters only when trade history is unavailable.
                Volume1HUSD = asset.Volume1H ?? 0,
                Volume24HUSD = asset.Volume24H ?? 0,
                RealTVLUSD = asset.TVL_USD
            };

            if (assetVolumes != null)
            {
                // Assets absent from the dictionary simply had no trades in the past 48h.
                assetVolumes.TryGetValue(asset.Index, out var windows);
                item.Volume1HUSD = windows?.Volume1H ?? 0;
                item.Volume1HUSDPrev = windows?.Volume1HPrev ?? 0;
                item.Volume24HUSD = windows?.Volume24H ?? 0;
                item.Volume24HUSDPrev = windows?.Volume24HPrev ?? 0;
                if (item.Volume1HUSDPrev > 0)
                {
                    item.Volume1HChangePercent = (item.Volume1HUSD - item.Volume1HUSDPrev.Value) / item.Volume1HUSDPrev.Value * 100m;
                }
                if (item.Volume24HUSDPrev > 0)
                {
                    item.Volume24HChangePercent = (item.Volume24HUSD - item.Volume24HUSDPrev.Value) / item.Volume24HUSDPrev.Value * 100m;
                }
            }

            if (asset.PriceUSD24H > 0 && asset.PriceUSD > 0)
            {
                item.PriceChange24HPercent = (asset.PriceUSD - asset.PriceUSD24H.Value) / asset.PriceUSD24H.Value * 100m;
            }

            if (tvl24hAgo != null)
            {
                // An asset absent from an existing 24h-ago snapshot did not exist (or held no
                // liquidity) back then — its baseline is 0, so its whole current TVL is the 24h
                // gain. Without this, newly funded tokens never show up in the value lists.
                tvl24hAgo.TryGetValue(asset.Index, out var oldTvl);
                item.RealTVLUSD24H = oldTvl;
                item.TVLChange24HUSD = asset.TVL_USD - oldTvl;
                if (oldTvl > 0)
                {
                    item.TVLChange24HPercent = (asset.TVL_USD - oldTvl) / oldTvl * 100m;
                }
            }

            return item;
        }

        /// <summary>
        /// Loads each candidate asset's real TVL close value at/near 24 hours ago from the durable
        /// Elasticsearch TVL-OHLC index, in a single batched query. Assets absent from the result are
        /// treated by <see cref="ToItem"/> as having had zero TVL 24h ago (same semantics as the old
        /// Redis-hourly-snapshot approach this replaced).
        /// </summary>
        private async Task<IReadOnlyDictionary<ulong, decimal>?> LoadTvl24hAgoAsync(IReadOnlyCollection<BiatecAsset> universe, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (_tvlOhlcRepository == null) return null;
            try
            {
                var assetIds = universe.Select(a => a.Index).ToList();
                if (assetIds.Count == 0) return null;
                var result = await _tvlOhlcRepository.GetTvlAtOrBeforeAsync(assetIds, now.AddHours(-24), cancellationToken);
                return result.Count > 0 ? result : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load 24h-ago TVL baseline from Elasticsearch");
                return null;
            }
        }
    }
}
