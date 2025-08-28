using AVMTradeReporter.Model.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Microsoft.Extensions.Logging;

namespace AVMTradeReporter.Repository
{
    public class OHLCRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<OHLCRepository> _logger;

        private static readonly (string code, TimeSpan span)[] _intervals = new[]
        {
            ("1m", TimeSpan.FromMinutes(1)),
            ("5m", TimeSpan.FromMinutes(5)),
            ("15m", TimeSpan.FromMinutes(15)),
            ("1h", TimeSpan.FromHours(1)),
            ("4h", TimeSpan.FromHours(4)),
            ("1d", TimeSpan.FromDays(1)),
            ("1w", TimeSpan.FromDays(7)),
            ("1M", TimeSpan.FromDays(31)) // approximate month bucket
        };

        public OHLCRepository(ElasticsearchClient client, ILogger<OHLCRepository> logger)
        {
            _elasticClient = client;
            _logger = logger;
            CreateTemplateAsync().Wait();
        }

        private async Task CreateTemplateAsync()
        {
            if (_elasticClient == null) return; // elastic disabled
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
            if (interval.TotalDays >= 7) // week or month approximation
            {
                if (interval.TotalDays >= 30) // month approx: first day of month
                {
                    return new DateTimeOffset(new DateTime(ts.Year, ts.Month, 1, 0,0,0, DateTimeKind.Utc));
                }
                // week: Monday as start (ISO)
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
            return new DateTimeOffset(ts.UtcDateTime.Date); // fallback
        }

        public async Task UpdateFromTradeAsync(Trade trade, CancellationToken cancellationToken)
        {
            if (_elasticClient == null) return; // disabled
            if (trade.TradeState != Model.Data.Enums.TxState.Confirmed) return;
            if (!trade.Timestamp.HasValue) return;
            // Build canonical pair (low id is asset A)
            var inId = trade.AssetIdIn;
            var outId = trade.AssetIdOut;
            // we only create OHLC if both have ids (native ALGO is 0 allowed)
            var aId = Math.Min(inId, outId);
            var bId = Math.Max(inId, outId);
            if (aId == bId) return;

            // Determine base/quote orientation for price. Use canonical A as base.
            // Trade volumes: we need how much base and quote were traded in this trade.
            decimal volBase = 0; decimal volQuote = 0; decimal? tradePrice = null;
            if (trade.AssetIdIn == aId && trade.AssetIdOut == bId)
            {
                volBase = trade.AssetAmountIn;
                volQuote = trade.AssetAmountOut;
            }
            else if (trade.AssetIdIn == bId && trade.AssetIdOut == aId)
            {
                volBase = trade.AssetAmountOut; // amount of A acquired
                volQuote = trade.AssetAmountIn; // amount of B spent
            }
            else
            {
                // Unexpected orientation with 0 asset? If one side equals aId/bId we handle above.
                return;
            }
            if (volBase > 0)
            {
                tradePrice = volQuote / volBase; // B per A
            }
            if (tradePrice == null) return;

            var ts = trade.Timestamp.Value.ToUniversalTime();

            foreach (var (code, span) in _intervals)
            {
                var bucketStart = GetBucketStart(ts, span);
                var docId = $"{aId}-{bId}-{code}-{bucketStart:yyyyMMddHHmmss}";
                var indexName = "ohlc"; // base index name

                var script = $@"if (ctx._source.open == null) {{ ctx._source.open = params.p; }} else if (ctx._source.open == 0) {{ ctx._source.open = params.p; }}
if (ctx._source.high == null || ctx._source.high < params.p) {{ ctx._source.high = params.p; }}
if (ctx._source.low == null || ctx._source.low > params.p) {{ ctx._source.low = params.p; }}
ctx._source.close = params.p;
ctx._source.volumeBase = (ctx._source.volumeBase == null ? 0 : ctx._source.volumeBase) + params.vb;
ctx._source.volumeQuote = (ctx._source.volumeQuote == null ? 0 : ctx._source.volumeQuote) + params.vq;
ctx._source.trades = (ctx._source.trades == null ? 0 : ctx._source.trades) + 1;
ctx._source.lastUpdated = params.now;";

                var upsert = new OHLC
                {
                    AssetIdA = aId,
                    AssetIdB = bId,
                    Interval = code,
                    StartTime = bucketStart,
                    Open = tradePrice,
                    High = tradePrice,
                    Low = tradePrice,
                    Close = tradePrice,
                    VolumeBase = volBase,
                    VolumeQuote = volQuote,
                    Trades = 1,
                    LastUpdated = ts
                };

                try
                {
                    var response = await _elasticClient.UpdateAsync<OHLC, object>(indexName, docId, u => u
                        .DocAsUpsert(true)
                        .Script(s => s.Source(script).Params(p => p
                            .Add("p", (double)tradePrice.Value)
                            .Add("vb", (double)volBase)
                            .Add("vq", (double)volQuote)
                            .Add("now", DateTimeOffset.UtcNow.UtcDateTime)))
                        .Upsert(upsert), cancellationToken);

                    if (!response.IsValidResponse)
                    {
                        _logger.LogWarning("Failed to update OHLC {docId}: {info}", docId, response.DebugInformation);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating OHLC {docId}", docId);
                }
            }
        }
    }
}
