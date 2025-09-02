using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using Elastic.Clients.Elasticsearch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/search")] // GET api/search?q=algo
    public class SearchController : ControllerBase
    {
        private readonly IAssetRepository _assetRepository;
        private readonly IPoolRepository _poolRepository;
        private readonly AggregatedPoolRepository _aggregatedPoolRepository;
        private readonly ElasticsearchClient? _elastic; // resolved optionally
        private readonly ILogger<SearchController> _logger;

        public SearchController(IAssetRepository assetRepository,
                                IPoolRepository poolRepository,
                                AggregatedPoolRepository aggregatedPoolRepository,
                                IServiceProvider serviceProvider,
                                ILogger<SearchController> logger)
        {
            _assetRepository = assetRepository;
            _poolRepository = poolRepository;
            _aggregatedPoolRepository = aggregatedPoolRepository;
            _elastic = serviceProvider.GetService<ElasticsearchClient>(); // may be null when ES not configured
            _logger = logger;
        }

        public class SearchResponse
        {
            public List<object> Assets { get; set; } = new();
            public List<object> Pools { get; set; } = new();
            public List<object> AggregatedPools { get; set; } = new();
            public List<object> Addresses { get; set; } = new();
            public List<object> Blocks { get; set; } = new();
        }

        [HttpGet]
        [ProducesResponseType(typeof(SearchResponse), 200)]
        public async Task<ActionResult<SearchResponse>> Search([FromQuery] string q)
        {
            var ct = HttpContext.RequestAborted;
            var res = new SearchResponse();
            if (string.IsNullOrWhiteSpace(q)) return Ok(res);
            q = q.Trim();

            // Assets: use in-memory / redis cache via repository (substring search)
            try
            {
                var assets = await _assetRepository.GetAssetsAsync(null, q, 0, 10, ct);
                foreach (var a in assets)
                {
                    res.Assets.Add(new { id = a.Index, name = a.Params?.Name, unit = a.Params?.UnitName, tvlUsd = a.TVL_USD, priceUsd = a.PriceUSD, type = "asset" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Asset search failed");
            }

            // Numeric interpretation (asset id, block, aggregated pool component)
            bool isNumber = ulong.TryParse(q, out var number);

            // Block search: only check against current indexer round
            try
            {
                var idx = TradeReporterBackgroundService.Indexer;
                if (isNumber && idx?.Round >= number)
                {
                    res.Blocks.Add(new { round = number, type = "block" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Block search check failed");
            }

            // Address heuristic (Algorand addresses length ~58, Base32 A-Z2-7; accept broader alnum) – just echo back
            if (q.Length >= 50 && q.Length <= 60 && q.All(c => char.IsLetterOrDigit(c)))
            {
                res.Addresses.Add(new { address = q, type = "address" });
            }

            // Pool search (by address exact + elastic wildcard if available)
            try
            {
                // Exact address via repository (returns 0 or 1)
                var exactPools = await _poolRepository.GetPoolsAsync(null, null, q, null, 10, ct);
                foreach (var p in exactPools.Take(10))
                {
                    res.Pools.Add(new { address = p.PoolAddress, appId = p.PoolAppId, a = p.AssetIdA, b = p.AssetIdB, protocol = p.Protocol.ToString(), type = "pool" });
                }

                if (_elastic != null && res.Pools.Count < 10)
                {
                    // Wildcard search on poolAddress and term on asset ids
                    var esPools = await _elastic.SearchAsync<Pool>(s => s
                        .Index("pools")
                        .Size(10)
                        .Query(qry => qry.Bool(b => b.Should(
                            sh => sh.Wildcard(w => w.Field(f => f.PoolAddress).Value($"*{q.ToLower()}*")),
                            isNumber ? sh => sh.Term(t => t.Field(f => f.AssetIdA).Value(number)) : null,
                            isNumber ? sh => sh.Term(t => t.Field(f => f.AssetIdB).Value(number)) : null
                        ))), ct);
                    if (esPools.IsValidResponse)
                    {
                        foreach (var p in esPools.Documents.Where(d => !res.Pools.Any(r => ((dynamic)r).address == d.PoolAddress)).Take(10 - res.Pools.Count))
                        {
                            res.Pools.Add(new { address = p.PoolAddress, appId = p.PoolAppId, a = p.AssetIdA, b = p.AssetIdB, protocol = p.Protocol.ToString(), type = "pool" });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pool search failed");
            }

            // Aggregated pool search
            try
            {
                if (isNumber)
                {
                    // find aggregated pools where number is one side
                    var list = _aggregatedPoolRepository.GetAllAggregatedPools(number, null, 0, 200) // 200 cap to limit scanning
                        .Where(p => p.AssetIdA == number || p.AssetIdB == number)
                        .Take(10)
                        .ToList();
                    foreach (var ap in list)
                    {
                        res.AggregatedPools.Add(new { a = ap.AssetIdA, b = ap.AssetIdB, pools = ap.PoolCount, type = "aggregatedPool" });
                    }
                }
                else if (q.Contains('-'))
                {
                    var parts = q.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && ulong.TryParse(parts[0], out var aId) && ulong.TryParse(parts[1], out var bId))
                    {
                        var ap = _aggregatedPoolRepository.GetAggregatedPool(aId, bId);
                        if (ap != null)
                        {
                            res.AggregatedPools.Add(new { a = ap.AssetIdA, b = ap.AssetIdB, pools = ap.PoolCount, type = "aggregatedPool" });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Aggregated pool search failed");
            }

            return Ok(res);
        }
    }
}
