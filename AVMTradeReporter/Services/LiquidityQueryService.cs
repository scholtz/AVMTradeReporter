using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using Elastic.Clients.Elasticsearch;

namespace AVMTradeReporter.Services
{
    public class LiquidityQueryService : ILiquidityQueryService
    {
        private readonly ElasticsearchClient? _elastic;
        private readonly ILogger<LiquidityQueryService> _logger;

        public LiquidityQueryService(IServiceProvider serviceProvider, ILogger<LiquidityQueryService> logger)
        {
            _elastic = serviceProvider.GetService<ElasticsearchClient>();
            _logger = logger;
        }

        public async Task<IEnumerable<Liquidity>> GetLiquidityAsync(
            ulong? assetIdA = null,
            ulong? assetIdB = null,
            string? txId = null,
            int offset = 0,
            int size = 100,
            CancellationToken cancellationToken = default)
        {
            var liquidityUpdates = new List<Liquidity>();

            try
            {
                if (_elastic == null)
                {
                    _logger.LogWarning("Elasticsearch client not available");
                    return liquidityUpdates;
                }

                var searchResponse = await _elastic.SearchAsync<Liquidity>(s => s
                    .Indices("liquidity")
                    .From(offset)
                    .Size(size)
                    .Sort(ss => ss.Field(f => f.Field(l => l.BlockId).Order(SortOrder.Desc)))
                    .Query(q => BuildQuery(q, assetIdA, assetIdB, txId)),
                    cancellationToken);

                if (searchResponse.IsValidResponse)
                {
                    liquidityUpdates.AddRange(searchResponse.Documents);
                }
                else
                {
                    _logger.LogError("Elasticsearch query failed: {Error}", searchResponse.DebugInformation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch liquidity updates from Elasticsearch");
            }

            return liquidityUpdates;
        }

        private static Elastic.Clients.Elasticsearch.QueryDsl.Query BuildQuery(
            Elastic.Clients.Elasticsearch.QueryDsl.QueryDescriptor<Liquidity> q,
            ulong? assetIdA,
            ulong? assetIdB,
            string? txId)
        {
            if (!string.IsNullOrWhiteSpace(txId))
            {
                // If txId is provided, search only by txId
                return q.Term(t => t.Field(f => f.TxId).Value(txId));
            }

            if (assetIdA.HasValue && assetIdB.HasValue)
            {
                // Both assets specified - require both conditions (AND logic)
                return q.Bool(b => b.Must(
                    m => m.Term(t => t.Field(f => f.AssetIdA).Value(assetIdA.Value)),
                    m => m.Term(t => t.Field(f => f.AssetIdB).Value(assetIdB.Value))
                ));
            }
            else if (assetIdA.HasValue || assetIdB.HasValue)
            {
                // Single asset specified - match either AssetIdA OR AssetIdB
                var assetId = assetIdA ?? assetIdB!.Value;
                return q.Bool(b => b.Should(
                    s => s.Term(t => t.Field(f => f.AssetIdA).Value(assetId)),
                    s => s.Term(t => t.Field(f => f.AssetIdB).Value(assetId))
                ));
            }

            // No filters provided, return all liquidity updates
            return q.MatchAll();
        }
    }
}