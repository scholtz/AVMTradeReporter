using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.DTO.OHLC;
using AVMTradeReporter.Models.Data;
using Microsoft.AspNetCore.SignalR;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using Algorand.Algod.Model;

[assembly: InternalsVisibleTo("AVMTradeReporterTests")]
namespace AVMTradeReporter.Repository
{
    public class OHLCRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<OHLCRepository> _logger;
        private readonly IAssetRepository? _assetRepository;
        private readonly ulong _usdReferenceAssetId;
        private readonly IHubContext<BiatecScanHub>? _hubContext;

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

        public OHLCRepository(ElasticsearchClient client, ILogger<OHLCRepository> logger, IAssetRepository? assetRepository = null, IOptions<AppConfiguration>? appConfig = null, IHubContext<BiatecScanHub>? hubContext = null)
        {
            _elasticClient = client;
            _logger = logger;
            _assetRepository = assetRepository;
            _usdReferenceAssetId = appConfig?.Value.UsdReferenceAssetId ?? 31566704UL;
            _hubContext = hubContext;
            CreateTemplateAsync().Wait();
        }

        private async Task CreateTemplateAsync()
        {
            if (_elasticClient == null) return;
            var request = new PutIndexTemplateRequest
            {
                Name = "ohlc_template",
                IndexPatterns = new[] { "ohlc-*" },
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
                    return new DateTimeOffset(new DateTime(ts.Year, ts.Month, 1, 0, 0, 0, DateTimeKind.Utc));
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

        internal async Task<IEnumerable<BucketSpec>> GetIntervalBuckets(Trade trade)
        {
            var result = new List<BucketSpec>();
            if (!trade.Timestamp.HasValue) return result;
            var aId = Math.Min(trade.AssetIdIn, trade.AssetIdOut);
            var bId = Math.Max(trade.AssetIdIn, trade.AssetIdOut);
            if (aId == bId) return result;

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
            else return result;
            if (volBase <= 0) return result;

            int decimalsA = 0;
            int decimalsB = 0;
            if (_assetRepository != null)
            {
                var assetA = await _assetRepository.GetAssetAsync(aId);
                var assetB = await _assetRepository.GetAssetAsync(bId);
                decimalsA = (int)(assetA?.Params?.Decimals ?? 0);
                decimalsB = (int)(assetB?.Params?.Decimals ?? 0);
            }
            decimal powA = (decimal)Math.Pow(10, decimalsA);
            decimal powB = (decimal)Math.Pow(10, decimalsB);
            decimal adjustedVolBase = volBase / powA;
            decimal adjustedVolQuote = volQuote / powB;
            decimal price = adjustedVolQuote / adjustedVolBase;
            var usdcAssetId = _usdReferenceAssetId;
            // Asset valuation: quote-per-base using raw on-chain volumes.
            // var price = volQuote / volBase;

            var ts = trade.Timestamp.Value.ToUniversalTime();
            foreach (var (code, span) in Intervals)
            {
                var bucketStart = GetBucketStart(ts, span);
                var docIdAsset = $"{aId}-{bId}-{code}-asset-{bucketStart:yyyyMMddHHmmss}";
                result.Add(new BucketSpec(code, bucketStart, docIdAsset, false, price, adjustedVolBase, adjustedVolQuote, aId, bId));

                // USD valuation: if trade has USD value, compute USD-per-base-unit.
                decimal? usdPriceBase = null;
                if (trade.ValueUSD.HasValue && adjustedVolBase > 0)
                {
                    usdPriceBase = trade.ValueUSD.Value / adjustedVolBase;
                }

                // The USD reference asset itself is included (docId "{usd}-{usd}-...-usd-...")
                // so its own USD price chart (e.g. USDC/USD) has data.
                if (usdPriceBase.HasValue)
                {
                    // For USD-valued series, keep volumeBase in base asset units, but express quote volume in USD.
                    var docIdUsd = $"{aId}-{usdcAssetId}-{code}-usd-{bucketStart:yyyyMMddHHmmss}";
                    var volumeUsd = usdPriceBase.Value * adjustedVolBase;
                    result.Add(new BucketSpec(code, bucketStart, docIdUsd, true, usdPriceBase.Value, adjustedVolBase, volumeUsd, aId, usdcAssetId));
                }

                // USD valuation: if trade has USD value, compute USD-per-base-unit.
                decimal? usdPriceQuote = null;
                if (trade.ValueUSD.HasValue && adjustedVolQuote > 0)
                {
                    usdPriceQuote = trade.ValueUSD.Value / adjustedVolQuote;
                }

                if (usdPriceQuote.HasValue)
                {
                    // For USD-valued series, keep volumeBase in base asset units, but express quote volume in USD.
                    var docIdUsd = $"{bId}-{usdcAssetId}-{code}-usd-{bucketStart:yyyyMMddHHmmss}";
                    var volumeUsd = usdPriceQuote.Value * adjustedVolQuote;
                    result.Add(new BucketSpec(code, bucketStart, docIdUsd, true, usdPriceQuote.Value, adjustedVolQuote, volumeUsd, bId, usdcAssetId));
                }
            }
            return result;
        }

        /// <summary>
        /// Collapses per-interval bucket specs into one real-time tick per distinct
        /// OHLC series (price/volume are identical across intervals of a series).
        /// </summary>
        internal static List<OhlcTickDto> GetTicks(IEnumerable<BucketSpec> buckets, DateTimeOffset timestamp)
        {
            return buckets
                .GroupBy(b => (b.AssetIdA, b.AssetIdB, b.InUsdValuation))
                .Select(g =>
                {
                    var f = g.First();
                    return new OhlcTickDto
                    {
                        AssetIdA = f.AssetIdA,
                        AssetIdB = f.AssetIdB,
                        InUSDValuation = f.InUsdValuation,
                        Price = f.Price,
                        VolumeBase = f.VolumeBase,
                        VolumeQuote = f.VolumeQuote,
                        Timestamp = timestamp.ToUnixTimeSeconds()
                    };
                })
                .ToList();
        }

        private async Task PublishTicksAsync(IEnumerable<BucketSpec> buckets, Trade trade, CancellationToken cancellationToken)
        {
            if (_hubContext == null || !trade.Timestamp.HasValue) return;
            try
            {
                foreach (var tick in GetTicks(buckets, trade.Timestamp.Value))
                {
                    var group = BiatecScanHub.OhlcGroupName(tick.AssetIdA, tick.AssetIdB);
                    await _hubContext.Clients.Group(group).SendAsync(BiatecScanHub.Subscriptions.OHLC, tick, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish OHLC ticks for trade {txId}", trade.TxId);
            }
        }

        public async Task UpdateFromTradeAsync(Trade trade, CancellationToken cancellationToken)
        {
            if (trade.TradeState != Models.Data.Enums.TxState.Confirmed) return;
            var buckets = await GetIntervalBuckets(trade);
            if (!buckets.Any()) return;

            await PublishTicksAsync(buckets, trade, cancellationToken);

            if (_elasticClient == null) return;

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
