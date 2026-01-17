using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AVMTradeReporterTests")]
namespace AVMTradeReporter.Repository
{
    public class OHLCRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<OHLCRepository> _logger;

        internal static readonly (string code, TimeSpan span)[] Intervals = new[]
        {
            ("1m", TimeSpan.FromMinutes(1)),
            ("5m", TimeSpan.FromMinutes(5)),
            ("15m", TimeSpan.FromMinutes(15)),
            ("1h", TimeSpan.FromHours(1)),
            ("4h", TimeSpan.FromHours(4)),
            ("1d", TimeSpan.FromDays(1)),
            ("1w", TimeSpan.FromDays(7)),
            ("1M", TimeSpan.FromDays(31))
        };

        public OHLCRepository(ElasticsearchClient client, ILogger<OHLCRepository> logger)
        {
            _elasticClient = client;
            _logger = logger;
            CreateTemplateAsync().Wait();
        }

        private async Task CreateTemplateAsync()
        {
            if (_elasticClient == null) return;
            var request = new PutIndexTemplateRequest
            {
                Name = "ohlc_template",
                IndexPatterns = new []{"ohlc-*"},
                Template = new IndexTemplateMapping
                {
                    Mappings = new TypeMapping
                    {
                        Properties = new Properties
                        {
                            {"assetIdA", new LongNumberProperty()},
                            {"assetIdB", new LongNumberProperty()},
                            {"interval", new KeywordProperty()},
                            {"inUSDValuation", new BooleanProperty()},
                            {"startTime", new DateProperty()},
                            {"open", new DoubleNumberProperty()},
                            {"high", new DoubleNumberProperty()},
                            {"low", new DoubleNumberProperty()},
                            {"close", new DoubleNumberProperty()},
                            {"volumeBase", new DoubleNumberProperty()},
                            {"volumeQuote", new DoubleNumberProperty()},
                            {"trades", new LongNumberProperty()},
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
                    _logger.LogWarning("Failed to create OHLC template: {info}", resp.DebugInformation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create OHLC template");
            }
        }

        private static DateTimeOffset GetBucketStart(DateTimeOffset ts, TimeSpan interval)
        {
            if (interval.TotalDays >= 7)
            {
                if (interval.TotalDays >= 30)
                {
                    return new DateTimeOffset(new DateTime(ts.Year, ts.Month, 1, 0,0,0, DateTimeKind.Utc));
                }
                int diff = (7 + (int)ts.UtcDateTime.DayOfWeek - (int)DayOfWeek.Monday) % 7;
                var monday = ts.UtcDateTime.Date.AddDays(-diff);
                return new DateTimeOffset(monday, TimeSpan.Zero);
            }
            if (interval.Hours >= 1)
            {
                var hours = (int)(Math.Floor(ts.UtcDateTime.Hour / interval.TotalHours) * interval.TotalHours);
                return new DateTimeOffset(new DateTime(ts.Year, ts.Month, ts.Day, hours, 0, 0, DateTimeKind.Utc));
            }
            if (interval.Minutes >= 1)
            {
                var minutes = (int)(Math.Floor(ts.UtcDateTime.Minute / interval.TotalMinutes) * interval.TotalMinutes);
                return new DateTimeOffset(new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, minutes, 0, DateTimeKind.Utc));
            }
            return new DateTimeOffset(ts.UtcDateTime.Date);
        }

        internal record BucketSpec(string Interval, DateTimeOffset BucketStart, string DocId, bool InUsdValuation, decimal Price, decimal VolumeBase, decimal VolumeQuote, ulong AssetIdA, ulong AssetIdB);

        internal static IEnumerable<BucketSpec> GetIntervalBuckets(Trade trade)
        {
            if (!trade.Timestamp.HasValue) yield break;
            var aId = Math.Min(trade.AssetIdIn, trade.AssetIdOut);
            var bId = Math.Max(trade.AssetIdIn, trade.AssetIdOut);
            if (aId == bId) yield break;

            decimal volBase; decimal volQuote;
            if (trade.AssetIdIn == aId && trade.AssetIdOut == bId)
            {
                volBase = trade.AssetAmountIn;
                volQuote = trade.AssetAmountOut;
            }
            else if (trade.AssetIdIn == bId && trade.AssetIdOut == aId)
            {
                volBase = trade.AssetAmountOut;
                volQuote = trade.AssetAmountIn;
            }
            else yield break;
            if (volBase <= 0) yield break;

            // Asset valuation: quote-per-base using raw on-chain volumes.
            var price = volQuote / volBase;

            // USD valuation: if trade has USD value, compute USD-per-base-unit.
            decimal? usdPrice = null;
            if (trade.ValueUSD.HasValue && volBase > 0)
            {
                usdPrice = trade.ValueUSD.Value / volBase;
            }

            var ts = trade.Timestamp.Value.ToUniversalTime();
            foreach (var (code, span) in Intervals)
            {
                var bucketStart = GetBucketStart(ts, span);
                var docIdAsset = $"{aId}-{bId}-{code}-asset-{bucketStart:yyyyMMddHHmmss}";
                yield return new BucketSpec(code, bucketStart, docIdAsset, false, price, volBase, volQuote, aId, bId);

                if (usdPrice.HasValue)
                {
                    // For USD-valued series, keep volumeBase in base asset units, but express quote volume in USD.
                    var docIdUsd = $"{aId}-{bId}-{code}-usd-{bucketStart:yyyyMMddHHmmss}";
                    var volumeUsd = trade.ValueUSD.Value;
                    yield return new BucketSpec(code, bucketStart, docIdUsd, true, usdPrice.Value, volBase, volumeUsd, aId, bId);
                }
            }
        }

        public async Task UpdateFromTradeAsync(Trade trade, CancellationToken cancellationToken)
        {
            if (_elasticClient == null) return;
            if (trade.TradeState != Models.Data.Enums.TxState.Confirmed) return;
            var buckets = GetIntervalBuckets(trade).ToList();
            if (!buckets.Any()) return;

            var bulkRequest = new BulkRequest("ohlc") { Operations = new BulkOperationsCollection() };
            var now = DateTimeOffset.UtcNow.UtcDateTime;
            foreach (var b in buckets)
            {
                var script = "if (ctx._source.open == null) { ctx._source.open = params.p; } else if (ctx._source.open == 0) { ctx._source.open = params.p; }" +
                             "if (ctx._source.high == null || ctx._source.high < params.p) { ctx._source.high = params.p; }" +
                             "if (ctx._source.low == null || ctx._source.low > params.p) { ctx._source.low = params.p; }" +
                             "ctx._source.close = params.p;" +
                             "ctx._source.volumeBase = (ctx._source.volumeBase == null ? 0 : ctx._source.volumeBase) + params.vb;" +
                             "ctx._source.volumeQuote = (ctx._source.volumeQuote == null ? 0 : ctx._source.volumeQuote) + params.vq;" +
                             "ctx._source.trades = (ctx._source.trades == null ? 0 : ctx._source.trades) + 1;" +
                             "ctx._source.lastUpdated = params.now;";
                var upsert = new OHLC
                {
                    AssetIdA = b.AssetIdA,
                    AssetIdB = b.AssetIdB,
                    Interval = b.Interval,
                    InUSDValuation = b.InUsdValuation,
                    StartTime = b.BucketStart,
                    Open = b.Price,
                    High = b.Price,
                    Low = b.Price,
                    Close = b.Price,
                    VolumeBase = b.VolumeBase,
                    VolumeQuote = b.VolumeQuote,
                    Trades = 1,
                    LastUpdated = b.BucketStart
                };
                bulkRequest.Operations.Add(new BulkUpdateOperation<OHLC, object>(upsert)
                {
                    Id = b.DocId,
                    Index = "ohlc",
                    Script = new Script
                    {
                        Source = script,
                        Params = new Dictionary<string, object>
                        {
                            {"p", (double)b.Price},
                            {"vb", (double)b.VolumeBase},
                            {"vq", (double)b.VolumeQuote},
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
                    _logger.LogWarning("Bulk OHLC update failure: {info}", resp.DebugInformation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk OHLC update exception");
            }
        }
    }
}
