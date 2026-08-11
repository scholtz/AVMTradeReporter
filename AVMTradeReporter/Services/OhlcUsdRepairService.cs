using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
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
    /// One-shot, idempotent repair of historical data polluted before the 2026-08-11
    /// trusted-anchor OHLC fix (see <see cref="Repository.OHLCRepository"/>) so the 7d price and
    /// 7d TVL charts read correctly without waiting for the window to roll:
    ///
    /// 1. USD price candles: every `-usd-` 1h candle of the last 8 days is rebuilt from the
    ///    never-polluted `-asset-` candles — directly from the asset↔UsdReferenceAsset pair
    ///    (constant $1 anchor) when it traded, otherwise from the asset↔ALGO pair scaled by
    ///    ALGO's USD close of the same hour. Existing USD candles with no rebuild basis (the
    ///    asset only traded against untrusted tokens that hour) are deleted; the timeseries
    ///    endpoint forward-fills those gaps as flat candles.
    /// 2. TVL snapshots: hourly Redis snapshots older than the configured cutoff are deleted —
    ///    before the EnvironmentKeyPrefix fix they were cross-written by two networks sharing
    ///    one Redis, so fields for asset ids existing on both networks hold garbage.
    ///
    /// A per-version marker key in Redis makes the run once-per-environment; bumping
    /// <see cref="RepairVersion"/> would re-run it after a future incident.
    /// </summary>
    public class OhlcUsdRepairService : IOhlcUsdRepairService
    {
        internal const string RepairVersion = "2026-08-11-trusted-anchor";
        private const string MarkerKeyBase = "maintenance:ohlc-usd-repair:";
        private const int HoursBack = 8 * 24;
        private const int BulkChunkSize = 1000;

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
            var from = now.AddHours(-(HoursBack + 1));
            var usdRef = _appConfig.UsdReferenceAssetId;

            // ALGO's historical USD price per hour, from the (0, usdRef) asset-rate candles —
            // the anchor for every asset that only traded against ALGO.
            var algoUsdCloseByHour = BuildForwardFilledCloses(
                await LoadAssetCandlesAsync(0UL, usdRef, from, cancellationToken), now);

            var assetIds = await LoadAssetIdsWithUsdCandlesAsync(from, cancellationToken);
            _logger.LogInformation("OHLC USD repair {version}: rebuilding 1h USD candles of {count} assets since {from}", RepairVersion, assetIds.Count, from);

            var operations = new List<IBulkOperation>();
            var repairedAssets = 0;
            var rebuiltCandles = 0;
            var deletedCandles = 0;

            foreach (var assetId in assetIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var direct = assetId == usdRef
                    ? new Dictionary<long, OHLC>() // (usdRef, usdRef) is not a pair; usdRef reprices via ALGO below
                    : await LoadAssetCandlesAsync(Math.Min(assetId, usdRef), Math.Max(assetId, usdRef), from, cancellationToken);
                var viaAlgo = assetId == 0UL
                    ? new Dictionary<long, OHLC>() // ALGO itself is priced by its direct usdRef pair
                    : await LoadAssetCandlesAsync(0UL, assetId, from, cancellationToken);
                var existing = await LoadExistingUsdCandleHoursAsync(assetId, usdRef, from, cancellationToken);

                var hours = new SortedSet<long>(direct.Keys);
                hours.UnionWith(viaAlgo.Keys);
                hours.UnionWith(existing);

                foreach (var hour in hours)
                {
                    OHLC? rebuilt = null;
                    if (direct.TryGetValue(hour, out var directCandle))
                    {
                        rebuilt = OhlcUsdRepairMath.RebuildUsdCandle(assetId, usdRef, directCandle, 1m, now);
                    }
                    if (rebuilt == null
                        && viaAlgo.TryGetValue(hour, out var algoCandle)
                        && algoUsdCloseByHour.TryGetValue(hour, out var algoUsd))
                    {
                        rebuilt = OhlcUsdRepairMath.RebuildUsdCandle(assetId, usdRef, algoCandle, algoUsd, now);
                    }

                    if (rebuilt != null)
                    {
                        operations.Add(new BulkIndexOperation<OHLC>(rebuilt) { Id = rebuilt.Id, Index = "ohlc" });
                        rebuiltCandles++;
                    }
                    else if (existing.Contains(hour))
                    {
                        var start = DateTimeOffset.FromUnixTimeSeconds(hour * 3600);
                        operations.Add(new BulkDeleteOperation($"{assetId}-{usdRef}-1h-usd-{start:yyyyMMddHHmmss}") { Index = "ohlc" });
                        deletedCandles++;
                    }
                }

                repairedAssets++;
                if (operations.Count >= BulkChunkSize)
                {
                    await FlushAsync(operations, cancellationToken);
                }
            }
            await FlushAsync(operations, cancellationToken);
            _logger.LogInformation("OHLC USD repair: rebuilt {rebuilt} and deleted {deleted} 1h USD candles across {assets} assets", rebuiltCandles, deletedCandles, repairedAssets);

            await CleanupTvlSnapshotsAsync(now, cancellationToken);
            await InvalidateTimeseriesCacheAsync(assetIds, cancellationToken);

            if (_redisDatabase != null)
            {
                await _redisDatabase.StringSetAsync(MarkerKey, now.ToString("o"));
            }
            return repairedAssets;
        }

        /// <summary>All asset ids that have 1h USD candles in the window — the series that may need repair.</summary>
        private async Task<List<ulong>> LoadAssetIdsWithUsdCandlesAsync(DateTimeOffset from, CancellationToken cancellationToken)
        {
            var response = await _elastic!.SearchAsync<OHLC>(s => s
                .Indices("ohlc")
                .Size(0)
                .Query(q => q.Bool(b => b.Filter(
                    f => f.Term(t => t.Field(o => o.Interval).Value("1h")),
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

        /// <summary>Loads the `-asset-` (exchange-rate) 1h candles of a canonical pair, newest doc per hour.</summary>
        private async Task<Dictionary<long, OHLC>> LoadAssetCandlesAsync(ulong assetIdA, ulong assetIdB, DateTimeOffset from, CancellationToken cancellationToken)
        {
            var response = await _elastic!.SearchAsync<OHLC>(s => s
                .Indices("ohlc")
                .Size(HoursBack + 16)
                .Sort(so => so.Field(o => o.StartTime))
                .Query(q => q.Bool(b => b
                    .Filter(
                        f => f.Term(t => t.Field(o => o.AssetIdA).Value(assetIdA)),
                        f => f.Term(t => t.Field(o => o.AssetIdB).Value(assetIdB)),
                        f => f.Term(t => t.Field(o => o.Interval).Value("1h")),
                        f => f.Range(r => r.Date(d => d.Field(o => o.StartTime).Gte(from.UtcDateTime))))
                    // Legacy asset-rate docs may predate the InUSDValuation field, so exclude
                    // usd docs instead of filtering on false.
                    .MustNot(mn => mn.Term(t => t.Field(o => o.InUSDValuation).Value(true))))),
                cancellationToken);

            var result = new Dictionary<long, OHLC>();
            if (!response.IsValidResponse) return result;
            foreach (var doc in response.Documents)
            {
                result[doc.StartTime.ToUnixTimeSeconds() / 3600] = doc;
            }
            return result;
        }

        private async Task<HashSet<long>> LoadExistingUsdCandleHoursAsync(ulong assetId, ulong usdRef, DateTimeOffset from, CancellationToken cancellationToken)
        {
            var response = await _elastic!.SearchAsync<OHLC>(s => s
                .Indices("ohlc")
                .Size(HoursBack + 16)
                .Query(q => q.Bool(b => b.Filter(
                    f => f.Term(t => t.Field(o => o.AssetIdA).Value(assetId)),
                    f => f.Term(t => t.Field(o => o.AssetIdB).Value(usdRef)),
                    f => f.Term(t => t.Field(o => o.Interval).Value("1h")),
                    f => f.Term(t => t.Field(o => o.InUSDValuation).Value(true)),
                    f => f.Range(r => r.Date(d => d.Field(o => o.StartTime).Gte(from.UtcDateTime)))))),
                cancellationToken);

            var result = new HashSet<long>();
            if (!response.IsValidResponse) return result;
            foreach (var doc in response.Documents)
            {
                result.Add(doc.StartTime.ToUnixTimeSeconds() / 3600);
            }
            return result;
        }

        /// <summary>Hour → close map, forward-filled so hours without ALGO/usdRef trades reuse the last known close.</summary>
        internal static Dictionary<long, decimal> BuildForwardFilledCloses(Dictionary<long, OHLC> candles, DateTimeOffset now)
        {
            var result = new Dictionary<long, decimal>();
            if (candles.Count == 0) return result;
            var firstHour = candles.Keys.Min();
            var lastHour = now.ToUnixTimeSeconds() / 3600;
            decimal? lastClose = null;
            for (var hour = firstHour; hour <= lastHour; hour++)
            {
                if (candles.TryGetValue(hour, out var candle) && candle.Close is > 0)
                {
                    lastClose = candle.Close.Value;
                }
                if (lastClose.HasValue) result[hour] = lastClose.Value;
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
            var oldestHour = now.ToUnixTimeSeconds() / 3600 - HoursBack - 24;
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
