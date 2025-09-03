using Algorand;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Logging;
using Elastic.Clients.Elasticsearch;

namespace AVMTradeReporter.Services
{
    public interface ISearchService
    {
        Task<SearchResponse> SearchAsync(string query, CancellationToken cancellationToken);
    }

    public class SearchResponse
    {
        public List<BiatecAsset> Assets { get; set; } = new();
        public List<Pool> Pools { get; set; } = new();
        public List<AggregatedPool> AggregatedPools { get; set; } = new();
        public List<string> Addresses { get; set; } = new();
        public List<ulong> Blocks { get; set; } = new();
        public List<Trade> Trades { get; set; } = new();
    }

    public class SearchService : ISearchService
    {
        private readonly IAssetRepository _assetRepository;
        private readonly IPoolRepository _poolRepository;
        private readonly AggregatedPoolRepository _aggregatedPoolRepository;
        private readonly ElasticsearchClient? _elastic;
        private readonly ILogger<SearchService> _logger;

        public SearchService(IAssetRepository assetRepository,
                             IPoolRepository poolRepository,
                             AggregatedPoolRepository aggregatedPoolRepository,
                             IServiceProvider serviceProvider,
                             ILogger<SearchService> logger)
        {
            _assetRepository = assetRepository;
            _poolRepository = poolRepository;
            _aggregatedPoolRepository = aggregatedPoolRepository;
            _elastic = serviceProvider.GetService<ElasticsearchClient>();
            _logger = logger;
        }

        public async Task<SearchResponse> SearchAsync(string query, CancellationToken cancellationToken)
        {
            var res = new SearchResponse();
            if (string.IsNullOrWhiteSpace(query)) return res;
            var q = query.Trim();

            // Assets
            try
            {
                var assets = await _assetRepository.GetAssetsAsync(null, q, 0, 10, cancellationToken);
                res.Assets.AddRange(assets);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Asset search failed");
            }

            bool isNumber = ulong.TryParse(q, out var number);

            // Block number heuristic
            try
            {
                var idx = TradeReporterBackgroundService.Indexer;
                if (isNumber && idx?.Round >= number)
                {
                    res.Blocks.Add(number);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Block search check failed");
            }

            // Address heuristic
            try
            {
                var address = new Address(q);
                if (address.EncodeAsString().Equals(q))
                {
                    res.Addresses.Add(q);
                }
            }
            catch { }

            // Pools
            try
            {
                var exactPools = await _poolRepository.GetPoolsAsync(null, null, q, null, 10, cancellationToken);
                foreach (var p in exactPools.Take(10)) res.Pools.Add(p);

                if (_elastic != null && res.Pools.Count < 10)
                {
                    var esPools = await _elastic.SearchAsync<Pool>(s => s
                        .Indices("pools")
                        .Size(10)
                        .Query(qry => qry.Bool(b => b.Should(
                            sh => sh.Wildcard(w => w.Field(f => f.PoolAddress).Value($"*{q.ToLower()}*")),
                            isNumber ? sh => sh.Term(t => t.Field(f => f.AssetIdA).Value(number)) : null,
                            isNumber ? sh => sh.Term(t => t.Field(f => f.AssetIdB).Value(number)) : null
                        ))), cancellationToken);

                    if (esPools.IsValidResponse)
                    {
                        foreach (var p in esPools.Documents
                            .Where(d => !res.Pools.Any(r => r.PoolAddress == d.PoolAddress))
                            .Take(10 - res.Pools.Count))
                        {
                            res.Pools.Add(p);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pool search failed");
            }

            // Aggregated pools
            try
            {
                if (isNumber)
                {
                    var list = _aggregatedPoolRepository.GetAllAggregatedPools(number, null, 0, 200)
                        .Where(p => p.AssetIdA == number || p.AssetIdB == number)
                        .Take(10)
                        .ToList();
                    res.AggregatedPools.AddRange(list);
                }
                else if (q.Contains('-'))
                {
                    var parts = q.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && ulong.TryParse(parts[0], out var aId) && ulong.TryParse(parts[1], out var bId))
                    {
                        var ap = _aggregatedPoolRepository.GetAggregatedPool(aId, bId);
                        if (ap != null) res.AggregatedPools.Add(ap);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Aggregated pool search failed");
            }

            // Trades
            try
            {
                if (_elastic != null)
                {
                    var esTrades = await _elastic.SearchAsync<Trade>(s => s
                        .Indices("trades")
                        .Size(10)
                        .Sort(ss => ss.Field(f => f.Field(t => t.BlockId).Order(SortOrder.Desc)))
                        .Query(qry => qry.Bool(b => b.Should(
                            sh => sh.Wildcard(w => w.Field(f => f.Trader).Value($"*{q.ToLower()}*")),
                            sh => sh.Wildcard(w => w.Field(f => f.PoolAddress).Value($"*{q.ToLower()}*")),
                            isNumber ? sh => sh.Term(t => t.Field(f => f.AssetIdIn).Value(number)) : null,
                            isNumber ? sh => sh.Term(t => t.Field(f => f.AssetIdOut).Value(number)) : null,
                            isNumber ? sh => sh.Term(t => t.Field(f => f.AssetAmountIn).Value(number)) : null,
                            isNumber ? sh => sh.Term(t => t.Field(f => f.AssetAmountOut).Value(number)) : null
                        ))), cancellationToken);

                    if (esTrades.IsValidResponse)
                    {
                        res.Trades.AddRange(esTrades.Documents);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Trade search failed");
            }

            return res;
        }
    }
}
