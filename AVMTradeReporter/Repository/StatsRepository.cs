using AVMTradeReporter.Models.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;

namespace AVMTradeReporter.Repository
{
    /// <summary>
    /// Queries Elasticsearch for aggregated DEX trading statistics using sum aggregations
    /// over the <c>trades</c> index.
    /// </summary>
    public class StatsRepository : IStatsRepository
    {
        private readonly ElasticsearchClient? _elasticClient;
        private readonly ILogger<StatsRepository> _logger;

        /// <param name="elasticClient">Elasticsearch client; may be <see langword="null"/> when not configured.</param>
        /// <param name="logger">Logger instance.</param>
        public StatsRepository(ElasticsearchClient elasticClient, ILogger<StatsRepository> logger)
        {
            _elasticClient = elasticClient;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<(double VolumeUSD, double FeesUSD, double FeesUSDProvider, double FeesUSDProtocol)> GetDexAggregationsAsync(
            string protocol,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default)
        {
            if (_elasticClient == null)
            {
                _logger.LogWarning("Elasticsearch client not available; returning zero aggregations for protocol {Protocol}", protocol);
                return (0, 0, 0, 0);
            }

            try
            {
                // Bool.Must is used here (consistent with existing codebase); scoring is irrelevant for agg-only queries.
                var mustClauses = new List<Action<QueryDescriptor<Trade>>>
                {
                    f => f.Term(t => t.Field("tradeState.keyword").Value("Confirmed")),
                    f => f.Term(t => t.Field("protocol.keyword").Value(protocol)),
                    f => f.Range(r => r.Date(d => d
                        .Field(fld => fld.Timestamp)
                        .Gte(from.ToString("o"))
                        .Lt(to.ToString("o"))
                    ))
                };

                var searchResponse = await _elasticClient.SearchAsync<Trade>(s => s
                    .Indices("trades")
                    .Size(0)
                    .Query(q => q.Bool(b => b.Must(mustClauses.ToArray())))
                    .Aggregations(a => a
                        .Add("volumeUSD", agg => agg.Sum(sum => sum.Field("valueUSD")))
                        .Add("feesUSD", agg => agg.Sum(sum => sum.Field("feesUSD")))
                        .Add("feesUSDProvider", agg => agg.Sum(sum => sum.Field("feesUSDProvider")))
                        .Add("feesUSDProtocol", agg => agg.Sum(sum => sum.Field("feesUSDProtocol")))
                    ),
                    cancellationToken);

                if (!searchResponse.IsValidResponse)
                {
                    _logger.LogError(
                        "Elasticsearch aggregation query failed for protocol {Protocol}: {Error}",
                        protocol, searchResponse.DebugInformation);
                    return (0, 0, 0, 0);
                }

                var volumeUSD = searchResponse.Aggregations?.GetSum("volumeUSD")?.Value ?? 0;
                var feesUSD = searchResponse.Aggregations?.GetSum("feesUSD")?.Value ?? 0;
                var feesUSDProvider = searchResponse.Aggregations?.GetSum("feesUSDProvider")?.Value ?? 0;
                var feesUSDProtocol = searchResponse.Aggregations?.GetSum("feesUSDProtocol")?.Value ?? 0;

                _logger.LogDebug(
                    "DEX aggregation for {Protocol} [{From} – {To}]: volume={Volume}, fees={Fees}, lpFees={LPFees}, protocolFees={ProtocolFees}",
                    protocol, from, to, volumeUSD, feesUSD, feesUSDProvider, feesUSDProtocol);

                return (volumeUSD, feesUSD, feesUSDProvider, feesUSDProtocol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute DEX aggregation query for protocol {Protocol}", protocol);
                return (0, 0, 0, 0);
            }
        }
    }
}
