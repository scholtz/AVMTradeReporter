using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Computes the "top assets" highlight lists (Popular, Trending, Top gainers/losers,
    /// Top value gainers/losers) from the in-memory asset cache and caches the result in Redis so the
    /// endpoint is served mostly from cache under heavy traffic.
    ///
    /// The 24h real-TVL change has no historical source anywhere else in the system, so this service
    /// also maintains hourly snapshots of each candidate asset's real TVL in Redis
    /// (<c>asset:tvl:hourly:{unixHour}</c>, ~30h retention) and diffs against the snapshot closest to
    /// 24 hours ago.
    /// </summary>
    public class TopAssetsService : ITopAssetsService
    {
        private const string CacheKey = "asset:top:summary";
        public const string TvlSnapshotKeyPrefix = "asset:tvl:hourly:";
        // 8 days so the 7d asset timeseries endpoint (AssetTimeseriesService) can always build a
        // full week of hourly TVL candles from these snapshots.
        private static readonly TimeSpan TvlSnapshotTtl = TimeSpan.FromDays(8);

        private readonly IAssetRepository _assetRepository;
        private readonly ILogger<TopAssetsService> _logger;
        private readonly IDatabase? _redisDatabase;
        private readonly AppConfiguration _appConfig;

        // Fallback served when Redis is unavailable/empty and a fresh computation fails or is redundant.
        private TopAssetsResponse? _lastComputed;

        public TopAssetsService(
            IAssetRepository assetRepository,
            ILogger<TopAssetsService> logger,
            IOptions<AppConfiguration> appConfig,
            IDatabase? redisDatabase = null)
        {
            _assetRepository = assetRepository;
            _logger = logger;
            _appConfig = appConfig.Value;
            _redisDatabase = redisDatabase;
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

            await StoreTvlSnapshotAsync(universe, now, cancellationToken);
            var tvl24hAgo = await LoadTvlSnapshotAsync(now, cancellationToken);

            var response = Build(universe, tvl24hAgo, listSize, now);
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
        /// Pure selection logic: builds the six highlight lists from the TVL-ordered candidate universe.
        /// Stable assets (StabilityIndex &gt; 0) are excluded from every list.
        /// </summary>
        public static TopAssetsResponse Build(
            IEnumerable<BiatecAsset> universeOrderedByTvl,
            IReadOnlyDictionary<ulong, decimal>? tvl24hAgo,
            int listSize,
            DateTimeOffset now)
        {
            var candidates = universeOrderedByTvl
                .Where(a => a.StabilityIndex == 0)
                .Select(a => ToItem(a, tvl24hAgo))
                .ToList();

            return new TopAssetsResponse
            {
                Popular = candidates
                    .Where(i => i.Volume24HUSD > 0)
                    .OrderByDescending(i => i.Volume24HUSD)
                    .Take(listSize).ToList(),
                Trending = candidates
                    .Where(i => i.Volume1HUSD > 0)
                    .OrderByDescending(i => i.Volume1HUSD)
                    .Take(listSize).ToList(),
                TopGainers = candidates
                    .Where(i => i.PriceChange24HPercent > 0)
                    .OrderByDescending(i => i.PriceChange24HPercent)
                    .Take(listSize).ToList(),
                TopLosers = candidates
                    .Where(i => i.PriceChange24HPercent < 0)
                    .OrderBy(i => i.PriceChange24HPercent)
                    .Take(listSize).ToList(),
                TopValueGainers = candidates
                    .Where(i => i.TVLChange24HPercent > 0)
                    .OrderByDescending(i => i.TVLChange24HPercent)
                    .Take(listSize).ToList(),
                TopValueLosers = candidates
                    .Where(i => i.TVLChange24HPercent < 0)
                    .OrderBy(i => i.TVLChange24HPercent)
                    .Take(listSize).ToList(),
                GeneratedAt = now
            };
        }

        private static TopAssetItem ToItem(BiatecAsset asset, IReadOnlyDictionary<ulong, decimal>? tvl24hAgo)
        {
            var item = new TopAssetItem
            {
                AssetId = asset.Index,
                Name = asset.Params?.Name,
                UnitName = asset.Params?.UnitName,
                Decimals = asset.Params?.Decimals,
                PriceUSD = asset.PriceUSD,
                PriceUSD24H = asset.PriceUSD24H,
                Volume1HUSD = asset.Volume1H ?? 0,
                Volume24HUSD = asset.Volume24H ?? 0,
                RealTVLUSD = asset.TVL_USD
            };

            if (asset.PriceUSD24H > 0 && asset.PriceUSD > 0)
            {
                item.PriceChange24HPercent = (asset.PriceUSD - asset.PriceUSD24H.Value) / asset.PriceUSD24H.Value * 100m;
            }

            if (tvl24hAgo != null && tvl24hAgo.TryGetValue(asset.Index, out var oldTvl))
            {
                item.RealTVLUSD24H = oldTvl;
                item.TVLChange24HUSD = asset.TVL_USD - oldTvl;
                if (oldTvl > 0)
                {
                    item.TVLChange24HPercent = (asset.TVL_USD - oldTvl) / oldTvl * 100m;
                }
            }

            return item;
        }

        private static long UnixHour(DateTimeOffset time) => time.ToUnixTimeSeconds() / 3600;

        /// <summary>
        /// Persists the current real TVL of every candidate asset into this hour's snapshot hash as an
        /// intra-hour OHLC value (see <see cref="TvlSnapshotCodec"/>). Runs on every ~5 minute
        /// recompute, merging into the hour's existing candle so high/low capture movement within the
        /// hour instead of just the last observation.
        /// </summary>
        private async Task StoreTvlSnapshotAsync(IEnumerable<BiatecAsset> universe, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (_redisDatabase == null) return;
            try
            {
                var key = TvlSnapshotKeyPrefix + UnixHour(now);
                var existingEntries = await _redisDatabase.HashGetAllAsync(key);
                var existing = existingEntries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

                var entries = universe
                    .Select(a =>
                    {
                        var field = a.Index.ToString(CultureInfo.InvariantCulture);
                        existing.TryGetValue(field, out var prev);
                        return new HashEntry(field, TvlSnapshotCodec.Merge(prev, a.TVL_USD));
                    })
                    .ToArray();
                if (entries.Length == 0) return;
                await _redisDatabase.HashSetAsync(key, entries);
                await _redisDatabase.KeyExpireAsync(key, TvlSnapshotTtl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store hourly TVL snapshot in Redis");
            }
        }

        /// <summary>
        /// Loads the TVL snapshot closest to 24 hours ago. Tries hour offsets fanning out from 24
        /// (24, 25, 23, 26, 22, ...) so short service outages around the 24h mark still resolve to a
        /// nearby snapshot instead of losing the whole "value gainers/losers" feature.
        /// </summary>
        private async Task<IReadOnlyDictionary<ulong, decimal>?> LoadTvlSnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (_redisDatabase == null) return null;
            var currentHour = UnixHour(now);
            foreach (var offset in new[] { 24, 25, 23, 26, 22, 27, 21, 28, 20 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var key = TvlSnapshotKeyPrefix + (currentHour - offset);
                    var entries = await _redisDatabase.HashGetAllAsync(key);
                    if (entries == null || entries.Length == 0) continue;

                    var result = new Dictionary<ulong, decimal>(entries.Length);
                    foreach (var entry in entries)
                    {
                        if (ulong.TryParse(entry.Name.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var assetId)
                            && TvlSnapshotCodec.TryParse(entry.Value.ToString(), out _, out _, out _, out var tvl))
                        {
                            result[assetId] = tvl;
                        }
                    }
                    if (result.Count > 0) return result;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load TVL snapshot from Redis (offset {offset}h)", offset);
                    return null;
                }
            }
            return null;
        }
    }
}
