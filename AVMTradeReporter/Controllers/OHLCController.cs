using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Repository;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OHLCController : ControllerBase
    {
        private readonly ElasticsearchClient _elastic;
        private readonly IAssetRepository _assetRepository;
        private readonly AggregatedPoolRepository _aggregatedPoolRepository;
        // Use case-sensitive comparer so '1m' (minute) and '1M' (month) are distinct.
        // Remove duplicate identical keys that caused ArgumentException at type initialization.
        private static readonly Dictionary<string, string> _resolutionMap = new(StringComparer.Ordinal)
        {
            {"1","1m"}, {"1m","1m"},
            {"5","5m"}, {"5m","5m"},
            {"15","15m"}, {"15m","15m"},
            {"60","1h"}, {"1h","1h"},
            {"240","4h"}, {"4h","4h"},
            {"D","1d"}, {"1D","1d"}, {"1d","1d"}, // day synonyms
            {"W","1w"}, {"1W","1w"}, {"1w","1w"}, // week synonyms
            {"M","1M"}, {"1M","1M"} // month
        };
        private static readonly string[] _supportedResolutions = new[] { "1", "5", "15", "60", "240", "1D", "1W", "1M" };

        public OHLCController(ElasticsearchClient elastic, IAssetRepository assetRepository, AggregatedPoolRepository aggregatedPoolRepository)
        {
            _elastic = elastic;
            _assetRepository = assetRepository;
            _aggregatedPoolRepository = aggregatedPoolRepository;
        }

        [HttpGet("config")]
        public IActionResult GetConfig()
        {
            return Ok(new
            {
                supports_search = true,
                supports_group_request = false,
                supports_marks = false,
                supports_timescale_marks = false,
                supports_time = true,
                supported_resolutions = _supportedResolutions
            });
        }

        [HttpGet("time")]
        public IActionResult GetTime()
        {
            var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return Ok(epoch);
        }

        private (ulong a, ulong b)? ParseSymbol(string? symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;
            var parts = symbol.Replace("_", "-").Split('-', '/', ':');
            if (parts.Length < 2) return null;
            if (ulong.TryParse(parts[0], out var a) && ulong.TryParse(parts[1], out var b))
            {
                return (a, b);
            }
            return null;
        }

        [HttpGet("symbols")]
        public async Task<IActionResult> GetSymbol([FromQuery] string symbol, CancellationToken ct)
        {
            var parsed = ParseSymbol(symbol);
            if (parsed == null) return NotFound();
            var (a, b) = parsed.Value;
            var assetA = await _assetRepository.GetAssetAsync(a, ct);
            var assetB = await _assetRepository.GetAssetAsync(b, ct);
            if (assetA == null || assetB == null) return NotFound();

            // Price scale derived from max decimals (TradingView expects integer price values / pricescale)
            var decA = assetA.Params?.Decimals ?? 6;
            var decB = assetB.Params?.Decimals ?? 6;
            var priceScale = (int)Math.Pow(10, Math.Max(decA, decB));

            var name = $"{a}-{b}";
            return Ok(new
            {
                name,
                ticker = name,
                description = $"{assetA.Params?.UnitName ?? a.ToString()} / {assetB.Params?.UnitName ?? b.ToString()}",
                type = "crypto",
                session = "24x7",
                exchange = "ALG",
                listed_exchange = "ALG",
                timezone = "Etc/UTC",
                format = "price",
                minmov = 1,
                minmov2 = 0,
                pricescale = priceScale,
                has_intraday = true,
                has_no_volume = false,
                volume_precision = 6,
                supported_resolutions = _supportedResolutions,
                data_status = "streaming"
            });
        }

        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string query, [FromQuery] string? type, [FromQuery] int limit = 30, CancellationToken ct = default)
        {
            query = query?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(query)) return Ok(Array.Empty<object>());
            // Search assets by unit name or name
            var assets = await _assetRepository.GetAssetsAsync(null, query, 0, limit, ct);
            var res = assets.Select(a => new
            {
                symbol = a.Index.ToString(),
                full_name = a.Index.ToString(),
                description = a.Params?.Name ?? a.Params?.UnitName ?? a.Index.ToString(),
                ticker = a.Index.ToString(),
                type = "crypto",
                exchange = "ALG"
            });
            return Ok(res);
        }

        [HttpGet("marks")]
        public IActionResult GetMarks() => Ok(Array.Empty<object>());

        [HttpGet("timescale_marks")]
        public IActionResult GetTimescaleMarks() => Ok(Array.Empty<object>());

        [HttpGet("quotes")]
        public IActionResult GetQuotes([FromQuery] string symbols)
        {
            // symbols is JSON array or comma separated; support simple comma separated list of pair ids like "123-0,0-31566704"
            var list = symbols?.Trim().Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
            var data = new List<object>();
            foreach (var raw in list)
            {
                var sym = raw.Replace("\"", "").Trim();
                var parsed = ParseSymbol(sym);
                if (parsed == null) continue;
                var (a, b) = parsed.Value;
                var pool = _aggregatedPoolRepository.GetAggregatedPool(a, b);
                if (pool == null) continue;
                AggregatedPool oriented = pool;
                if (pool.AssetIdA != a || pool.AssetIdB != b) oriented = (pool.AssetIdA == b && pool.AssetIdB == a) ? pool.Reverse() : pool;
                decimal price = oriented.VirtualSumALevel1 > 0 ? oriented.VirtualSumBLevel1 / oriented.VirtualSumALevel1 : 0m;
                data.Add(new
                {
                    s = "ok",
                    n = sym,
                    v = new
                    {
                        ch = 0m,
                        chp = 0m,
                        short_name = sym,
                        exchange = "ALG",
                        description = sym,
                        price,
                        volume = oriented.TVL_A + oriented.TVL_B,
                        bid = price,
                        ask = price,
                        high_price = price,
                        low_price = price
                    }
                });
            }
            return Ok(new { s = "ok", d = data });
        }

        /// <summary>
        /// TradingView compatible history endpoint
        /// GET api/ohlc/history?assetA=..&assetB=..&resolution=1&from=unix&to=unix
        /// </summary>
        [HttpGet("history")]
        public async Task<IActionResult> GetHistory([FromQuery] ulong assetA, [FromQuery] ulong assetB, [FromQuery] string resolution, [FromQuery] long from, [FromQuery] long to, CancellationToken ct)
        {
            if (_elastic == null) return Ok(new { s = "no_data" });
            if (assetA == assetB) return Ok(new { s = "no_data" });
            if (!_resolutionMap.TryGetValue(resolution ?? string.Empty, out var interval)) return BadRequest("Unsupported resolution");

            var fromDt = DateTimeOffset.FromUnixTimeSeconds(from).UtcDateTime;
            var toDt = DateTimeOffset.FromUnixTimeSeconds(to).UtcDateTime;
            if (toDt <= fromDt) return Ok(new { s = "no_data" });

            var a = Math.Min(assetA, assetB);
            var b = Math.Max(assetA, assetB);
            var invert = (a != assetA); // caller provided reversed order

            // Build date range query manually
            Query dateRange = new DateRangeQuery
            {
                Field = Infer.Field<OHLC>(f => f.StartTime),
                Gte = fromDt,
                Lte = toDt
            };

            // Query ES
            var search = await _elastic.SearchAsync<OHLC>(s => s
                .Indices("ohlc")
                .Size(5000) // upper limit
                .Sort(ss => ss.Field(f => f.StartTime))
                .Query(q => q.Bool(bq => bq
                    .Filter(
                        q.Term(t => t.Field(f => f.AssetIdA).Value(a)),
                        q.Term(t => t.Field(f => f.AssetIdB).Value(b)),
                        q.Term(t => t.Field(f => f.Interval).Value(interval)),
                        dateRange
                    ))), ct);

            if (!search.IsValidResponse || search.Hits.Count == 0)
            {
                return Ok(new { s = "no_data" });
            }

            var t = new List<long>();
            var o = new List<decimal>();
            var h = new List<decimal>();
            var l = new List<decimal>();
            var c = new List<decimal>();
            var v = new List<decimal>(); // base volume in requested orientation

            foreach (var doc in search.Documents.OrderBy(d => d.StartTime))
            {
                if (doc.Open == null || doc.High == null || doc.Low == null || doc.Close == null) continue;
                var ts = doc.StartTime.ToUnixTimeSeconds();
                t.Add(ts);
                if (!invert)
                {
                    o.Add(doc.Open.Value); h.Add(doc.High.Value); l.Add(doc.Low.Value); c.Add(doc.Close.Value);
                    v.Add((doc.VolumeBase ?? 0));
                }
                else
                {
                    // invert price = 1/price ; volumes swap
                    decimal SafeInv(decimal x) => x == 0 ? 0 : 1 / x;
                    o.Add(SafeInv(doc.Open.Value));
                    h.Add(SafeInv(doc.Low.Value)); // high/low swap when inverting
                    l.Add(SafeInv(doc.High.Value));
                    c.Add(SafeInv(doc.Close.Value));
                    v.Add((doc.VolumeQuote ?? 0));
                }
            }

            if (t.Count == 0)
            {
                return Ok(new { s = "no_data" });
            }

            return Ok(new
            {
                s = "ok",
                t,
                o,
                h,
                l,
                c,
                v
            });
        }
    }
}
