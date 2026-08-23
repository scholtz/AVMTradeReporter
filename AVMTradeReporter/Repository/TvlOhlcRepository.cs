using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Model.DTO.TVL;
using AVMTradeReporter.Models.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.AspNetCore.SignalR;

namespace AVMTradeReporter.Repository
{
    /// <summary>
    /// Durable Elasticsearch-backed TVL (real total value locked, USD) OHLC candles for a single
    /// asset over time, plus real-time SignalR tick emission - the TVL counterpart of
    /// <see cref="OHLCRepository"/> for price. Simpler than price OHLC: there is no base/quote asset
    /// pair and no traded volume/trade-count, just one asset id's USD value over time.
    ///
    /// Unlike <see cref="OHLCRepository"/> (whose index template's IndexPatterns "ohlc-*" never
    /// actually matches the literal index name "ohlc" it writes to - a known, NOT-repeated bug here),
    /// this repository's template pattern matches the literal index name it uses ("tvlohlc").
    /// </summary>
    public class TvlOhlcRepository
    {
        internal const string IndexName = "tvlohlc";

        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<TvlOhlcRepository> _logger;
        private readonly IHubContext<BiatecScanHub>? _hubContext;

        public TvlOhlcRepository(ElasticsearchClient client, ILogger<TvlOhlcRepository> logger, IHubContext<BiatecScanHub>? hubContext = null)
        {
            _elasticClient = client;
            _logger = logger;
            _hubContext = hubContext;
            CreateTemplateAsync().Wait();
        }

        private async Task CreateTemplateAsync()
        {
            if (_elasticClient == null) return;
            var request = new PutIndexTemplateRequest
            {
                Name = "tvlohlc_template",
                IndexPatterns = new[] { IndexName },
                Template = new IndexTemplateMapping
                {
                    Mappings = new TypeMapping
                    {
                        Properties = new Properties
                        {
                            {"assetId", new LongNumberProperty()},
                            {"interval", new KeywordProperty()},
                            {"startTime", new DateProperty()},
                            {"open", new DoubleNumberProperty()},
                            {"high", new DoubleNumberProperty()},
                            {"low", new DoubleNumberProperty()},
                            {"close", new DoubleNumberProperty()},
                            {"lastUpdated", new DateProperty()}
                        }
                    }
                }
            };
            try
            {
                var resp = await _elasticClient.Indices.PutIndexTemplateAsync(request);
                if (!resp.IsValidResponse)
                {
                    _logger.LogWarning("Failed to create TVL OHLC template: {info}", resp.DebugInformation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create TVL OHLC template");
            }
        }

        internal record BucketSpec(string Interval, DateTimeOffset BucketStart, string DocId, ulong AssetId, decimal TvlUsd);

        /// <summary>
        /// Computes the per-interval bucket set for one TVL observation. Pure/synchronous so it is
        /// unit-testable without an Elasticsearch client - mirrors
        /// <see cref="OHLCRepository.GetIntervalBuckets"/> but for a single asset's USD value
        /// (no pair, no volume).
        /// </summary>
        internal static List<BucketSpec> GetIntervalBuckets(ulong assetId, decimal tvlUsd, DateTimeOffset timestamp)
        {
            var ts = timestamp.ToUniversalTime();
            var result = new List<BucketSpec>(OHLCRepository.Intervals.Length);
            foreach (var (code, span) in OHLCRepository.Intervals)
            {
                var bucketStart = OHLCRepository.GetBucketStart(ts, span);
                var docId = $"{assetId}-{code}-{bucketStart:yyyyMMddHHmmss}";
                result.Add(new BucketSpec(code, bucketStart, docId, assetId, tvlUsd));
            }
            return result;
        }

        /// <summary>
        /// Builds the single real-time tick for a TVL update. Unlike price OHLC (which can fan out
        /// into an asset-valuation tick plus up to two USD-valuation ticks per trade), a TVL change is
        /// already a single USD series per asset, so there is exactly one tick per update.
        /// </summary>
        internal static TvlTickDto GetTick(ulong assetId, decimal tvlUsd, DateTimeOffset timestamp) => new()
        {
            AssetId = assetId,
            Tvl = tvlUsd,
            Timestamp = timestamp.ToUnixTimeSeconds()
        };

        private async Task PublishTickAsync(ulong assetId, decimal tvlUsd, DateTimeOffset timestamp, CancellationToken cancellationToken)
        {
            if (_hubContext == null) return;
            try
            {
                var tick = GetTick(assetId, tvlUsd, timestamp);
                var group = BiatecScanHub.TvlGroupName(assetId);
                await _hubContext.Clients.Group(group).SendAsync(BiatecScanHub.Subscriptions.TVL, tick, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish TVL tick for asset {assetId}", assetId);
            }
        }

        /// <summary>
        /// Records a TVL observation: publishes the real-time tick (BEFORE the ES write, so live UI
        /// updates are not blocked on ES, same ordering as <see cref="OHLCRepository.UpdateFromTradeAsync"/>),
        /// then upserts one bucket document per interval via a painless script (open set only once,
        /// high/low widen, close overwrites, lastUpdated stamped).
        /// </summary>
        public async Task UpdateFromTvlChangeAsync(ulong assetId, decimal tvlUsd, DateTimeOffset timestamp, CancellationToken cancellationToken)
        {
            var buckets = GetIntervalBuckets(assetId, tvlUsd, timestamp);
            if (buckets.Count == 0) return;

            await PublishTickAsync(assetId, tvlUsd, timestamp, cancellationToken);

            if (_elasticClient == null) return;

            var bulkRequest = new BulkRequest(IndexName) { Operations = new BulkOperationsCollection() };
            var now = DateTimeOffset.UtcNow.UtcDateTime;
            foreach (var b in buckets)
            {
                var script = "if (ctx._source.open == null) { ctx._source.open = params.v; } else if (ctx._source.open == 0) { ctx._source.open = params.v; }" +
                             "if (ctx._source.high == null || ctx._source.high < params.v) { ctx._source.high = params.v; }" +
                             "if (ctx._source.low == null || ctx._source.low > params.v) { ctx._source.low = params.v; }" +
                             "ctx._source.close = params.v;" +
                             "ctx._source.lastUpdated = params.now;";
                var upsert = new TvlOhlc
                {
                    AssetId = b.AssetId,
                    Interval = b.Interval,
                    StartTime = b.BucketStart,
                    Open = b.TvlUsd,
                    High = b.TvlUsd,
                    Low = b.TvlUsd,
                    Close = b.TvlUsd,
                    LastUpdated = b.BucketStart
                };
                bulkRequest.Operations.Add(new BulkUpdateOperation<TvlOhlc, object>(upsert)
                {
                    Id = b.DocId,
                    Index = IndexName,
                    Script = new Script
                    {
                        Source = script,
                        Params = new Dictionary<string, object>
                        {
                            {"v", (double)b.TvlUsd},
                            {"now", now}
                        }
                    },
                    Upsert = upsert
                });
            }
            try
            {
                var resp = await _elasticClient.BulkAsync(bulkRequest, cancellationToken);
                if (!resp.IsValidResponse)
                {
                    _logger.LogWarning("Bulk TVL OHLC update failure: {info}", resp.DebugInformation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk TVL OHLC update exception");
            }
        }

        /// <summary>
        /// Loads hourly TVL candles for one asset over the trailing <paramref name="hoursBack"/> hours,
        /// forward-filling hours with no TVL-changing activity from the last known close so the
        /// sparkline has no holes. Mirrors AssetTimeseriesService's (former) price-candle query shape.
        /// </summary>
        public async Task<TimeseriesCandles> GetCandlesAsync(ulong assetId, DateTimeOffset now, int hoursBack, CancellationToken cancellationToken)
        {
            var candles = new TimeseriesCandles();
            if (_elasticClient == null) return candles;

            var fromDt = now.UtcDateTime.AddHours(-(hoursBack + 1));
            var request = new SearchRequest<TvlOhlc>(IndexName)
            {
                Size = hoursBack + 8,
                Sort = new List<SortOptions>
                {
                    new SortOptions { Field = new FieldSort { Field = Infer.Field<TvlOhlc>(f => f.StartTime) } }
                },
                Query = new BoolQuery
                {
                    Filter = new List<Query>
                    {
                        new TermQuery { Field = Infer.Field<TvlOhlc>(f => f.AssetId), Value = assetId },
                        new TermQuery { Field = Infer.Field<TvlOhlc>(f => f.Interval), Value = "1h" },
                        new DateRangeQuery { Field = Infer.Field<TvlOhlc>(f => f.StartTime), Gte = fromDt }
                    }
                }
            };

            try
            {
                var search = await _elasticClient.SearchAsync<TvlOhlc>(request, cancellationToken);
                if (!search.IsValidResponse) return candles;

                var docsByHour = search.Documents
                    .Where(d => d.Open.HasValue && d.High.HasValue && d.Low.HasValue && d.Close.HasValue)
                    .GroupBy(d => d.StartTime.ToUnixTimeSeconds() / 3600)
                    .ToDictionary(g => g.Key, g => g.OrderBy(d => d.StartTime).Last());
                if (docsByHour.Count == 0) return candles;

                var currentHour = now.ToUnixTimeSeconds() / 3600;
                decimal? lastClose = null;
                for (var hour = currentHour - hoursBack; hour <= currentHour; hour++)
                {
                    decimal o, h, l, c;
                    if (docsByHour.TryGetValue(hour, out var doc))
                    {
                        o = doc.Open!.Value; h = doc.High!.Value; l = doc.Low!.Value; c = doc.Close!.Value;
                    }
                    else if (lastClose.HasValue)
                    {
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
                _logger.LogWarning(ex, "Failed to load 1h TVL candles for asset {AssetId}", assetId);
            }
            return candles;
        }

        /// <summary>
        /// Batch-fetches each asset's nearest-at-or-before-<paramref name="targetTime"/> TVL close
        /// value, for a set of candidate asset ids, in a single Elasticsearch query - used by
        /// TopAssetsService's 24h-ago "Top Liquidity Gainers/Losers" baseline instead of N+1 lookups.
        /// Since TVL ticks are now written whenever an asset's real TVL actually changes (event
        /// driven, not on a fixed cadence), different assets can have their most recent hourly bucket
        /// at different times before <paramref name="targetTime"/> - so this looks back over a bounded
        /// window (<paramref name="lookbackHours"/>) and picks, per asset, the doc with the latest
        /// StartTime at or before the target. An asset with no doc in the window is simply absent from
        /// the result (the caller treats that the same as a zero baseline, matching the old Redis
        /// snapshot behavior for newly funded assets).
        /// </summary>
        public async Task<Dictionary<ulong, decimal>> GetTvlAtOrBeforeAsync(
            IReadOnlyCollection<ulong> assetIds,
            DateTimeOffset targetTime,
            CancellationToken cancellationToken,
            int lookbackHours = 12)
        {
            var result = new Dictionary<ulong, decimal>();
            if (_elasticClient == null || assetIds.Count == 0) return result;

            var toDt = targetTime.UtcDateTime;
            var fromDt = toDt.AddHours(-lookbackHours);
            var request = new SearchRequest<TvlOhlc>(IndexName)
            {
                Size = assetIds.Count * (lookbackHours + 1),
                Sort = new List<SortOptions>
                {
                    new SortOptions { Field = new FieldSort { Field = Infer.Field<TvlOhlc>(f => f.StartTime), Order = SortOrder.Desc } }
                },
                Query = new BoolQuery
                {
                    Filter = new List<Query>
                    {
                        new TermsQuery
                        {
                            Field = Infer.Field<TvlOhlc>(f => f.AssetId),
                            Terms = new TermsQueryField(assetIds.Select(id => FieldValue.Long((long)id)).ToArray())
                        },
                        new TermQuery { Field = Infer.Field<TvlOhlc>(f => f.Interval), Value = "1h" },
                        new DateRangeQuery { Field = Infer.Field<TvlOhlc>(f => f.StartTime), Gte = fromDt, Lte = toDt }
                    }
                }
            };

            try
            {
                var search = await _elasticClient.SearchAsync<TvlOhlc>(request, cancellationToken);
                if (!search.IsValidResponse) return result;

                // Docs arrive sorted by StartTime descending, so the first one seen per asset id is
                // the nearest-at-or-before match.
                foreach (var doc in search.Documents)
                {
                    if (!doc.Close.HasValue) continue;
                    if (result.ContainsKey(doc.AssetId)) continue;
                    result[doc.AssetId] = doc.Close.Value;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to batch-load TVL-at-or-before for {count} assets", assetIds.Count);
            }
            return result;
        }
    }
}
