using AVMTradeReporter.Model.Data;
using Elastic.Clients.Elasticsearch;

namespace AVMTradeReporter.Services
{
    public class TradeQueryService : ITradeQueryService
    {
        private readonly ElasticsearchClient? _elastic;
        private readonly ILogger<TradeQueryService> _logger;

        public TradeQueryService(IServiceProvider serviceProvider, ILogger<TradeQueryService> logger)
        {
            _elastic = serviceProvider.GetService<ElasticsearchClient>();
            _logger = logger;
        }

        public async Task<IEnumerable<Trade>> GetTradesAsync(
            ulong? assetIdIn = null,
            ulong? assetIdOut = null,
            string? txId = null,
            int offset = 0,
            int size = 100,
            CancellationToken cancellationToken = default)
        {
            var trades = new List<Trade>();

            try
            {
                if (_elastic == null)
                {
                    _logger.LogWarning("Elasticsearch client not available");
                    return trades;
                }

                // Build query based on parameters
                var query = BuildQuery(assetIdIn, assetIdOut, txId);

                var searchResponse = await _elastic.SearchAsync<Trade>(s => s
                    .Indices("trades")
                    .Query(query)
                    .From(offset)
                    .Size(size)
                    .Sort(ss => ss.Field(f => f.Field(t => t.BlockId).Order(SortOrder.Desc))),
                    cancellationToken);

                if (searchResponse.IsValidResponse)
                {
                    trades.AddRange(searchResponse.Documents);
                }
                else
                {
                    _logger.LogError("Elasticsearch query failed: {Error}", searchResponse.DebugInformation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch trades from Elasticsearch");
            }

            return trades;
        }

        private Action<QueryDescriptor<Trade>> BuildQuery(ulong? assetIdIn, ulong? assetIdOut, string? txId)
        {
            return q =>
            {
                if (!string.IsNullOrWhiteSpace(txId))
                {
                    // If txId is provided, search only by txId
                    return q.Term(t => t.Field(f => f.TxId).Value(txId));
                }

                if (assetIdIn.HasValue && assetIdOut.HasValue)
                {
                    // Both assets specified - require both conditions (AND logic)
                    return q.Bool(b => b.Must(
                        m => m.Term(t => t.Field(f => f.AssetIdIn).Value(assetIdIn.Value)),
                        m => m.Term(t => t.Field(f => f.AssetIdOut).Value(assetIdOut.Value))
                    ));
                }
                else if (assetIdIn.HasValue || assetIdOut.HasValue)
                {
                    // Single asset specified - match either assetIdIn OR assetIdOut
                    var assetId = assetIdIn ?? assetIdOut!.Value;
                    return q.Bool(b => b.Should(
                        s => s.Term(t => t.Field(f => f.AssetIdIn).Value(assetId)),
                        s => s.Term(t => t.Field(f => f.AssetIdOut).Value(assetId))
                    ));
                }

                // No filters provided, return all trades
                return q.MatchAll();
            };
        }
    }
}