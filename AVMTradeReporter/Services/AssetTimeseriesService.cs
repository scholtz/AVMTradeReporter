using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Builds the 7 day hourly asset timeseries (USD price OHLC from the Elasticsearch "ohlc" index,
    /// real TVL OHLC from the Elasticsearch "tvlohlc" index written by <see cref="AVMTradeReporter.Repository.TvlOhlcRepository"/>)
    /// and caches each asset's series in Redis for ~1 hour so list pages (assets, favorites, pools)
    /// can request sparkline data for a whole page of assets and be served almost entirely from cache.
    /// </summary>
    public class AssetTimeseriesService : IAssetTimeseriesService
    {
        public const string CacheKeyBase = "asset:timeseries:7d:";
        private string CacheKeyPrefix => _appConfig.Redis.EnvironmentKeyPrefix + CacheKeyBase;
        private const int HoursBack = 7 * 24;
        // Slightly over an hour so the hourly background refresh normally replaces an entry before it
        // ever expires; on-demand computed entries for long-tail assets just expire and are recomputed
        // by the first request of the next hour.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(70);

        private readonly ElasticsearchClient? _elastic;
        private readonly IAssetRepository _assetRepository;
        private readonly ILogger<AssetTimeseriesService> _logger;
        private readonly IDatabase? _redisDatabase;
        private readonly AppConfiguration _appConfig;
        private readonly TvlOhlcRepository? _tvlOhlcRepository;

        public AssetTimeseriesService(
            IAssetRepository assetRepository,
            ILogger<AssetTimeseriesService> logger,
            IOptions<AppConfiguration> appConfig,
            ElasticsearchClient? elastic = null,
            IDatabase? redisDatabase = null,
            TvlOhlcRepository? tvlOhlcRepository = null)
        {
            _assetRepository = assetRepository;
            _logger = logger;
            _appConfig = appConfig.Value;
            _elastic = elastic;
            _redisDatabase = redisDatabase;
            _tvlOhlcRepository = tvlOhlcRepository;
        }

        public async Task<List<AssetTimeseries7D>> GetTimeseriesAsync(IReadOnlyCollection<ulong> assetIds, CancellationToken cancellationToken = default)
        {
            var ids = assetIds.Distinct().ToList();
            var result = new Dictionary<ulong, AssetTimeseries7D>(ids.Count);

            if (_redisDatabase != null && ids.Count > 0)
            {
                try
                {
                    var keys = ids.Select(id => (RedisKey)(CacheKeyPrefix + id)).ToArray();
                    var cached = await _redisDatabase.StringGetAsync(keys);
                    for (var i = 0; i < ids.Count; i++)
                    {
                        if (!cached[i].HasValue) continue;
                        var item = JsonSerializer.Deserialize<AssetTimeseries7D>((string)cached[i]!);
                        if (item != null) result[ids[i]] = item;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read asset timeseries from Redis cache");
                }
            }

            var missing = ids.Where(id => !result.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                var computed = await ComputeAndCacheAsync(missing, cancellationToken);
                foreach (var item in computed) result[item.AssetId] = item;
            }

            // Preserve request order.
            return ids.Where(result.ContainsKey).Select(id => result[id]).ToList();
        }

        public async Task<int> ComputeAndCacheUniverseAsync(CancellationToken cancellationToken = default)
        {
            var universeSize = Math.Max(1, _appConfig.AssetTimeseries?.UniverseSize ?? 200);
            // GetAssetsAsync orders by real TVL descending — the same universe the list pages show first.
            var universe = (await _assetRepository.GetAssetsAsync(null, null, 0, universeSize, cancellationToken)).ToList();
            var computed = await ComputeAndCacheAsync(universe.Select(a => a.Index).ToList(), cancellationToken);
            return computed.Count;
        }

        private async Task<List<AssetTimeseries7D>> ComputeAndCacheAsync(IReadOnlyList<ulong> assetIds, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;

            var results = new List<AssetTimeseries7D>(assetIds.Count);
            using var throttle = new SemaphoreSlim(8);
            var tasks = assetIds.Select(async id =>
            {
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    var series = new AssetTimeseries7D
                    {
                        AssetId = id,
                        Price = await LoadPriceCandlesAsync(id, now, cancellationToken),
                        Tvl = _tvlOhlcRepository != null
                            ? await _tvlOhlcRepository.GetCandlesAsync(id, now, HoursBack, cancellationToken)
                            : new TimeseriesCandles(),
                        GeneratedAt = now
                    };
                    lock (results) results.Add(series);
                    return series;
                }
                finally
                {
                    throttle.Release();
                }
            }).ToArray();

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute timeseries for some assets");
            }

            await StoreInCacheAsync(results, cancellationToken);
            return results;
        }

        private async Task<TimeseriesCandles> LoadPriceCandlesAsync(ulong assetId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var candles = new TimeseriesCandles();
            // USD price candles are stored as (assetId, UsdReferenceAssetId); the reference asset
            // itself has no candles (it is $1 by definition), so its price series stays empty.
            if (_elastic == null || assetId == _appConfig.UsdReferenceAssetId) return candles;

            var fromDt = now.UtcDateTime.AddHours(-(HoursBack + 1));
            var request = new SearchRequest<OHLC>("ohlc")
            {
                Size = HoursBack + 8,
                Sort = new List<SortOptions>
                {
                    new SortOptions { Field = new FieldSort { Field = Infer.Field<OHLC>(f => f.StartTime) } }
                },
                Query = new BoolQuery
                {
                    Filter = new List<Query>
                    {
                        new TermQuery { Field = Infer.Field<OHLC>(f => f.AssetIdA), Value = assetId },
                        new TermQuery { Field = Infer.Field<OHLC>(f => f.AssetIdB), Value = _appConfig.UsdReferenceAssetId },
                        new TermQuery { Field = Infer.Field<OHLC>(f => f.Interval), Value = "1h" },
                        new TermQuery { Field = Infer.Field<OHLC>(f => f.InUSDValuation), Value = true },
                        new DateRangeQuery { Field = Infer.Field<OHLC>(f => f.StartTime), Gte = fromDt }
                    }
                }
            };

            try
            {
                var search = await _elastic.SearchAsync<OHLC>(request, cancellationToken);
                if (!search.IsValidResponse) return candles;

                var docsByHour = search.Documents
                    .Where(d => d.Open.HasValue && d.High.HasValue && d.Low.HasValue && d.Close.HasValue)
                    .GroupBy(d => d.StartTime.ToUnixTimeSeconds() / 3600)
                    .ToDictionary(g => g.Key, g => g.OrderBy(d => d.StartTime).Last());
                if (docsByHour.Count == 0) return candles;

                var currentHour = now.ToUnixTimeSeconds() / 3600;
                decimal? lastClose = null;
                for (var hour = currentHour - HoursBack; hour <= currentHour; hour++)
                {
                    decimal o, h, l, c;
                    if (docsByHour.TryGetValue(hour, out var doc))
                    {
                        o = doc.Open!.Value; h = doc.High!.Value; l = doc.Low!.Value; c = doc.Close!.Value;
                    }
                    else if (lastClose.HasValue)
                    {
                        // Hours without trades: carry the last close forward as a flat candle.
                        o = h = l = c = lastClose.Value;
                    }
                    else
                    {
                        continue;
                    }
                    candles.T.Add(hour * 3600);
                    candles.O.Add(o);
                    candles.H.Add(h);
                    candles.L.Add(l);
                    candles.C.Add(c);
                    lastClose = c;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load 1h USD price candles for asset {AssetId}", assetId);
            }
            return candles;
        }

        private async Task StoreInCacheAsync(IReadOnlyCollection<AssetTimeseries7D> items, CancellationToken cancellationToken)
        {
            if (_redisDatabase == null || items.Count == 0) return;
            try
            {
                var writes = items
                    .Select(item => _redisDatabase.StringSetAsync(
                        CacheKeyPrefix + item.AssetId,
                        JsonSerializer.Serialize(item),
                        CacheTtl))
                    .ToArray();
                await Task.WhenAll(writes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store asset timeseries in Redis cache");
            }
        }
    }
}
