using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AVMTradeReporter.Services
{
    public interface IOhlcUsdRepairService
    {
        /// <summary>Runs the one-shot historical repair. Returns the number of assets whose USD series were rebuilt, or -1 when skipped.</summary>
        Task<int> RepairAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// One-shot, idempotent repair of historical USD OHLC data polluted before the 2026-08-11
    /// trusted-anchor ingestion fix (see <see cref="Repository.OHLCRepository"/>):
    ///
    /// 1. USD price candles: EVERY stored interval (1m…1M) of every `-usd-` series is rebuilt
    ///    from the `-asset-` candles of the same interval — directly from the
    ///    asset↔UsdReferenceAsset pair (constant $1 anchor) when it traded, otherwise from the
    ///    asset↔ALGO pair scaled by ALGO's USD close of the same bucket. The first repair
    ///    (2026-08-11) only rebuilt 1h × 8 days for the 7d sparklines; the charts read the other
    ///    intervals too, so e.g. the 4h chart stayed visibly "stripy" — hence this full-span,
    ///    all-interval version. Rebuilt wicks are clamped into a band around the candle body
    ///    (<see cref="AppConfiguration.OhlcTrustedPriceBandFactor"/>) so off-market dust prints
    ///    stored inside historical pair candles' High/Low don't resurface. Existing USD candles
    ///    with no rebuild basis (the asset only traded against untrusted tokens in that bucket)
    ///    are deleted; the timeseries endpoint forward-fills such gaps as flat candles.
    /// 2. TVL snapshots: hourly Redis snapshots older than the configured cutoff are deleted —
    ///    before the EnvironmentKeyPrefix fix they were cross-written by two networks sharing
    ///    one Redis, so fields for asset ids existing on both networks hold garbage.
    ///
    /// A per-version marker key in Redis makes the run once-per-environment; bumping
    /// <see cref="RepairVersion"/> would re-run it after a future incident.
    /// </summary>
    public class OhlcUsdRepairService : IOhlcUsdRepairService
    {
        internal const string RepairVersion = "2026-08-13-all-intervals";
        private const string MarkerKeyBase = "maintenance:ohlc-usd-repair:";
        private const int BulkChunkSize = 1000;
        private const int SearchPageSize = 5000;

        private readonly ILogger<OhlcUsdRepairService> _logger;
        private readonly AppConfiguration _appConfig;
        private readonly ElasticsearchClient? _elastic;
        private readonly IDatabase? _redisDatabase;

        public OhlcUsdRepairService(
            ILogger<OhlcUsdRepairService> logger,
            IOptions<AppConfiguration> appConfig,
            ElasticsearchClient? elastic = null,
            IDatabase? redisDatabase = null)
        {
            _logger = logger;
            _appConfig = appConfig.Value;
            _elastic = elastic;
            _redisDatabase = redisDatabase;
        }

        private string MarkerKey => _appConfig.Redis.EnvironmentKeyPrefix + MarkerKeyBase + RepairVersion;

        internal readonly record struct RepairStep(string Interval, TimeSpan Window);

        /// <summary>
        /// One rebuild step per stored interval: fine intervals (1m/5m/15m) use the shorter
        /// window, the coarse ones reach back over the whole polluted span.
        /// </summary>
        internal static IReadOnlyList<RepairStep> GetRepairPlan(OhlcRepairConfiguration config)
        {
            var fine = new HashSet<string> { "1m", "5m", "15m" };
            return OHLCRepository.Intervals
                .Select(i => new RepairStep(i.code, TimeSpan.FromDays(fine.Contains(i.code) ? config.FineWindowDays : config.CoarseWindowDays)))
                .ToList();
        }

        public async Task<int> RepairAsync(CancellationToken cancellationToken = default)
        {
            if (_elastic == null)
            {
                _logger.LogInformation("OHLC USD repair skipped: Elasticsearch is not configured");
                return -1;
            }

            if (_redisDatabase != null && await _redisDatabase.KeyExistsAsync(MarkerKey))
            {
                _logger.LogInformation("OHLC USD repair {version} already completed in this environment, skipping", RepairVersion);
                return -1;
            }

            var now = DateTimeOffset.UtcNow;
            var usdRef = _appConfig.UsdReferenceAssetId;
            var wickBand = _appConfig.OhlcTrustedPriceBandFactor;
            var allRepairedAssets = new HashSet<ulong>();
            var repairedAssets = 0;
            var rebuiltCandles = 0;
            var deletedCandles = 0;
            var operations = new List<IBulkOperation>();

            foreach (var step in GetRepairPlan(_appConfig.OhlcRepair))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var interval = step.Interval;
                var from = now - step.Window;

                // ALGO's historical USD price per bucket, from the (0, usdRef) asset-rate
                // candles — the anchor for every asset that only traded against ALGO.
                var algoRateCandles = await LoadAssetCandlesAsync(0UL, usdRef, interval, from, cancellationToken);

                var assetIds = await LoadAssetIdsWithUsdCandlesAsync(interval, from, cancellationToken);
                _logger.LogInformation("OHLC USD repair {version}: rebuilding {interval} USD candles of {count} assets since {from}", RepairVersion, interval, assetIds.Count, from);

                foreach (var assetId in assetIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var direct = assetId == usdRef
                        ? new Dictionary<long, OHLC>() // (usdRef, usdRef) is not a pair; usdRef reprices via ALGO below
                        : await LoadAssetCandlesAsync(Math.Min(assetId, usdRef), Math.Max(assetId, usdRef), interval, from, cancellationToken);
                    var viaAlgo = assetId == 0UL
                        ? new Dictionary<long, OHLC>() // ALGO itself is priced by its direct usdRef pair
                        : await LoadAssetCandlesAsync(0UL, assetId, interval, from, cancellationToken);
                    var existing = await LoadExistingUsdCandleBucketsAsync(assetId, usdRef, interval, from, cancellationToken);

                    var buckets = new SortedSet<long>(direct.Keys);
                    buckets.UnionWith(viaAlgo.Keys);
                    buckets.UnionWith(existing);
                    var algoUsdCloseByBucket = ForwardFillCloses(algoRateCandles, buckets);

                    foreach (var bucket in buckets)
                    {
                        OHLC? rebuilt = null;
                        if (direct.TryGetValue(bucket, out var directCandle))
                        {
                            rebuilt = OhlcUsdRepairMath.RebuildUsdCandle(assetId, usdRef, directCandle, 1m, now, wickBand);
                        }
                        if (rebuilt == null
                            && viaAlgo.TryGetValue(bucket, out var algoCandle)
                            && algoUsdCloseByBucket.TryGetValue(bucket, out var algoUsd))
                        {
                            rebuilt = OhlcUsdRepairMath.RebuildUsdCandle(assetId, usdRef, algoCandle, algoUsd, now, wickBand);
                        }

                        if (rebuilt != null)
                        {
                            operations.Add(new BulkIndexOperation<OHLC>(rebuilt) { Id = rebuilt.Id, Index = "ohlc" });
                            rebuiltCandles++;
                        }
                        else if (existing.Contains(bucket))
                        {
                            var start = DateTimeOffset.FromUnixTimeSeconds(bucket);
                            operations.Add(new BulkDeleteOperation($"{assetId}-{usdRef}-{interval}-usd-{start:yyyyMMddHHmmss}") { Index = "ohlc" });
                            deletedCandles++;
                        }
                    }

                    if (allRepairedAssets.Add(assetId)) repairedAssets++;
                    if (operations.Count >= BulkChunkSize)
                    {
                        await FlushAsync(operations, cancellationToken);
                    }
                }
            }
            await FlushAsync(operations, cancellationToken);
            _logger.LogInformation("OHLC USD repair: rebuilt {rebuilt} and deleted {deleted} USD candles across {assets} assets and all intervals", rebuiltCandles, deletedCandles, repairedAssets);

            await CleanupTvlSnapshotsAsync(now, cancellationToken);
            await InvalidateTimeseriesCacheAsync(allRepairedAssets, cancellationToken);

            if (_redisDatabase != null)
            {
                await _redisDatabase.StringSetAsync(MarkerKey, now.ToString("o"));
            }
            return repairedAssets;
        }

        /// <summary>All asset ids that have USD candles of this interval in the window — the series that may need repair.</summary>
        private async Task<List<ulong>> LoadAssetIdsWithUsdCandlesAsync(string interval, DateTimeOffset from, CancellationToken cancellationToken)
        {
            var response = await _elastic!.SearchAsync<OHLC>(s => s
                .Indices("ohlc")
                .Size(0)
                .Query(q => q.Bool(b => b.Filter(
                    f => f.Term(t => t.Field(o => o.Interval).Value(interval)),
                    f => f.Term(t => t.Field(o => o.InUSDValuation).Value(true)),
                    f => f.Range(r => r.Date(d => d.Field(o => o.StartTime).Gte(from.UtcDateTime))))))
                .Aggregations(a => a.Add("assets", agg => agg.Terms(t => t.Field(o => o.AssetIdA).Size(10000)))),
                cancellationToken);

            if (!response.IsValidResponse)
            {
                _logger.LogWarning("OHLC USD repair: failed to enumerate assets with USD candles: {info}", response.DebugInformation);
                return new List<ulong>();
            }
            var buckets = response.Aggregations?.GetLongTerms("assets")?.Buckets;
            return buckets == null ? new List<ulong>() : buckets.Select(b => (ulong)b.Key).ToList();
        }

        /// <summary>
        /// Loads the `-asset-` (exchange-rate) candles of a canonical pair for one interval,
        /// keyed by bucket-start unix seconds. Pages by StartTime: fine intervals over the fine
        /// window exceed Elasticsearch's 10k result window, and one doc exists per bucket, so
        /// StartTime is a strictly increasing cursor.
        /// </summary>
        private async Task<Dictionary<long, OHLC>> LoadAssetCandlesAsync(ulong assetIdA, ulong assetIdB, string interval, DateTimeOffset from, CancellationToken cancellationToken)
        {
            var result = new Dictionary<long, OHLC>();
            var cursor = from.UtcDateTime.AddSeconds(-1);
            while (true)
            {
                var cursorFrom = cursor;
                var response = await _elastic!.SearchAsync<OHLC>(s => s
                    .Indices("ohlc")
                    .Size(SearchPageSize)
                    .Sort(so => so.Field(o => o.StartTime))
                    .Query(q => q.Bool(b => b
                        .Filter(
                            f => f.Term(t => t.Field(o => o.AssetIdA).Value(assetIdA)),
                            f => f.Term(t => t.Field(o => o.AssetIdB).Value(assetIdB)),
                            f => f.Term(t => t.Field(o => o.Interval).Value(interval)),
                            f => f.Range(r => r.Date(d => d.Field(o => o.StartTime).Gt(cursorFrom))))
                        // Legacy asset-rate docs may predate the InUSDValuation field, so exclude
                        // usd docs instead of filtering on false.
                        .MustNot(mn => mn.Term(t => t.Field(o => o.InUSDValuation).Value(true))))),
                    cancellationToken);

                if (!response.IsValidResponse || response.Documents.Count == 0) return result;
                foreach (var doc in response.Documents)
                {
                    result[doc.StartTime.ToUnixTimeSeconds()] = doc;
                }
                if (response.Documents.Count < SearchPageSize) return result;
                cursor = response.Documents.Last().StartTime.UtcDateTime;
            }
        }

        private async Task<HashSet<long>> LoadExistingUsdCandleBucketsAsync(ulong assetId, ulong usdRef, string interval, DateTimeOffset from, CancellationToken cancellationToken)
        {
            var result = new HashSet<long>();
            var cursor = from.UtcDateTime.AddSeconds(-1);
            while (true)
            {
                var cursorFrom = cursor;
                var response = await _elastic!.SearchAsync<OHLC>(s => s
                    .Indices("ohlc")
                    .Size(SearchPageSize)
                    .Sort(so => so.Field(o => o.StartTime))
                    .Query(q => q.Bool(b => b.Filter(
                        f => f.Term(t => t.Field(o => o.AssetIdA).Value(assetId)),
                        f => f.Term(t => t.Field(o => o.AssetIdB).Value(usdRef)),
                        f => f.Term(t => t.Field(o => o.Interval).Value(interval)),
                        f => f.Term(t => t.Field(o => o.InUSDValuation).Value(true)),
                        f => f.Range(r => r.Date(d => d.Field(o => o.StartTime).Gt(cursorFrom)))))),
                    cancellationToken);

                if (!response.IsValidResponse || response.Documents.Count == 0) return result;
                foreach (var doc in response.Documents)
                {
                    result.Add(doc.StartTime.ToUnixTimeSeconds());
                }
                if (response.Documents.Count < SearchPageSize) return result;
                cursor = response.Documents.Last().StartTime.UtcDateTime;
            }
        }

        /// <summary>
        /// Bucket-key → close map for the requested keys, forward-filling gaps with the last
        /// known close. Keys are bucket-start unix seconds, so the fill works for any interval —
        /// including weeks and calendar months, whose buckets are not arithmetically spaced.
        /// Keys before the first candle get no value (no anchor exists yet).
        /// </summary>
        internal static Dictionary<long, decimal> ForwardFillCloses(Dictionary<long, OHLC> candles, IEnumerable<long> neededKeys)
        {
            var result = new Dictionary<long, decimal>();
            if (candles.Count == 0) return result;

            var candleKeys = candles.Keys.OrderBy(k => k).ToList();
            decimal? lastClose = null;
            var index = 0;
            foreach (var key in neededKeys.Distinct().OrderBy(k => k))
            {
                while (index < candleKeys.Count && candleKeys[index] <= key)
                {
                    if (candles[candleKeys[index]].Close is > 0 and var close)
                    {
                        lastClose = close;
                    }
                    index++;
                }
                if (lastClose.HasValue) result[key] = lastClose.Value;
            }
            return result;
        }

        private async Task FlushAsync(List<IBulkOperation> operations, CancellationToken cancellationToken)
        {
            if (operations.Count == 0) return;
            var request = new BulkRequest("ohlc") { Operations = new BulkOperationsCollection() };
            foreach (var op in operations) request.Operations.Add(op);
            operations.Clear();

            var response = await _elastic!.BulkAsync(request, cancellationToken);
            if (!response.IsValidResponse || response.Errors)
            {
                _logger.LogWarning("OHLC USD repair: bulk write reported errors: {info}", response.DebugInformation);
            }
        }

        /// <summary>Deletes hourly TVL snapshot keys older than the configured cutoff (cross-network polluted).</summary>
        private async Task CleanupTvlSnapshotsAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            var cutoff = _appConfig.OhlcRepair.TvlSnapshotCutoff;
            if (_redisDatabase == null || cutoff == null) return;

            var cutoffHour = cutoff.Value.ToUnixTimeSeconds() / 3600;
            var oldestHour = now.ToUnixTimeSeconds() / 3600 - (long)TimeSpan.FromDays(_appConfig.OhlcRepair.CoarseWindowDays).TotalHours - 24;
            var deletes = new List<Task<bool>>();
            for (var hour = oldestHour; hour < cutoffHour; hour++)
            {
                var key = _appConfig.Redis.EnvironmentKeyPrefix + TopAssetsService.TvlSnapshotKeyBase + hour;
                deletes.Add(_redisDatabase.KeyDeleteAsync(key));
            }
            if (deletes.Count == 0) return;
            await Task.WhenAll(deletes);
            _logger.LogInformation("OHLC USD repair: deleted {count} pre-cutoff hourly TVL snapshot keys (cutoff {cutoff})", deletes.Count(d => d.Result), cutoff);
        }

        /// <summary>
        /// Drops the cached 7d series of every repaired asset so the next request/refresh serves
        /// rebuilt data immediately; remaining entries expire on their own ~70 minute TTL.
        /// </summary>
        private async Task InvalidateTimeseriesCacheAsync(IReadOnlyCollection<ulong> assetIds, CancellationToken cancellationToken)
        {
            if (_redisDatabase == null || assetIds.Count == 0) return;
            var keys = assetIds
                .Select(id => (RedisKey)(_appConfig.Redis.EnvironmentKeyPrefix + AssetTimeseriesService.CacheKeyBase + id))
                .ToArray();
            await _redisDatabase.KeyDeleteAsync(keys);
        }
    }
}
