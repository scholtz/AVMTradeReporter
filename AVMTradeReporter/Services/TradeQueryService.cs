using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using Elastic.Clients.Elasticsearch;
using static Elastic.Clients.Elasticsearch.Field;

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

                var searchResponse = await _elastic.SearchAsync<Trade>(s => s
                    .Indices("trades")
                    .From(offset)
                    .Size(size)
                    .Sort(ss => ss.Field(f => f.Field(t => t.BlockId).Order(SortOrder.Desc)))
                    .Query(q => BuildQuery(q, assetIdIn, assetIdOut, txId)),
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

        public async Task<Dictionary<string, (decimal Volume1H, decimal Volume24H, decimal Volume7D)>> GetPoolVolumesAsync(IEnumerable<string> poolAddresses, CancellationToken cancellationToken = default)
        {
            var volumes = new Dictionary<string, (decimal Volume1H, decimal Volume24H, decimal Volume7D)>();

            if (_elastic == null)
            {
                _logger.LogWarning("Elasticsearch client not available for volume calculation");
                return volumes;
            }

            var poolAddressSet = new HashSet<string>(poolAddresses);

            var now = DateTimeOffset.UtcNow;

            // Periods
            var periods = new[]
            {
                (Hours: 1, Key: "1H"),
                (Hours: 24, Key: "24H"),
                (Hours: 168, Key: "7D") // 7*24
            };

            foreach (var period in periods)
            {
                var startTime = now.AddHours(-period.Hours);

                try
                {
                    // Fetch trades in the period
                    var searchResponse = await _elastic.SearchAsync<Trade>(s => s
                        .Indices("trades")
                        .Size(100000) // Increased size to handle more trades per period
                        .Query(q => q
                            .Bool(b => b
                                .Must(
                                    m => m.Range(r => r.Date(dr => dr.Field(f => f.Timestamp).Gte(startTime.ToString("o")))),
                                    m => m.Terms(t => t.Field(f => f.PoolAddress).Terms(poolAddressSet.Select(p => FieldValue.String(p)).ToArray()))
                                )
                            )
                        ),
                        cancellationToken);

                    if (searchResponse.IsValidResponse)
                    {
                        _logger.LogDebug("Fetched {Count} trades for period {Period}", searchResponse.Documents.Count, period.Key);
                        var tradesInPeriod = searchResponse.Documents.Where(t => t.ValueUSD.HasValue);
                        var grouped = tradesInPeriod.GroupBy(t => t.PoolAddress)
                            .ToDictionary(g => g.Key, g => g.Sum(t => t.ValueUSD!.Value));

                        foreach (var kv in grouped)
                        {
                            var poolAddress = kv.Key;
                            var volume = kv.Value;

                            if (!volumes.ContainsKey(poolAddress))
                            {
                                volumes[poolAddress] = (Volume1H: 0m, Volume24H: 0m, Volume7D: 0m);
                            }

                            var current = volumes[poolAddress];
                            volumes[poolAddress] = period.Key switch
                            {
                                "1H" => (volume, current.Volume24H, current.Volume7D),
                                "24H" => (current.Volume1H, volume, current.Volume24H),
                                "7D" => (current.Volume1H, current.Volume24H, volume),
                                _ => current
                            };
                        }
                    }
                    else
                    {
                        _logger.LogError("Elasticsearch query failed for {period}: {Error}", period.Key, searchResponse.DebugInformation);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to calculate volumes for {period}", period.Key);
                }
            }

            return volumes;
        }

        private static Elastic.Clients.Elasticsearch.QueryDsl.Query BuildQuery(
            Elastic.Clients.Elasticsearch.QueryDsl.QueryDescriptor<Trade> q,
            ulong? assetIdIn,
            ulong? assetIdOut,
            string? txId)
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
        }
    }
}




