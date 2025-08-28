using AVMTradeReporter.Model.Data;
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
        private static readonly Dictionary<string, string> _resolutionMap = new(StringComparer.OrdinalIgnoreCase)
        {
            {"1","1m"}, {"1m","1m"},
            {"5","5m"}, {"5m","5m"},
            {"15","15m"}, {"15m","15m"},
            {"60","1h"}, {"1h","1h"},
            {"240","4h"}, {"4h","4h"},
            {"D","1d"}, {"1D","1d"}, {"1d","1d"},
            {"W","1w"}, {"1W","1w"}, {"1w","1w"},
            {"M","1M"}, {"1M","1M"}
        };

        public OHLCController(ElasticsearchClient elastic)
        {
            _elastic = elastic;
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
