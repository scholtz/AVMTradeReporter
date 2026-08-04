using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using System.Collections.Concurrent;

namespace AVMTradeReporter.Repository
{
    /// <summary>
    /// ES-backed repository for <see cref="AssetStat"/> rows, following the same in-memory-cache +
    /// Elasticsearch-index pattern used by <see cref="AggregatedPoolRepository"/> and <see cref="PoolRepository"/>.
    /// </summary>
    public class AssetStatRepository : IAssetStatRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<AssetStatRepository> _logger;

        private static readonly ConcurrentDictionary<(ulong AssetId, DEXProtocol? Protocol), AssetStat> _cache = new();

        public AssetStatRepository(
            ElasticsearchClient elasticClient,
            ILogger<AssetStatRepository> logger)
        {
            _elasticClient = elasticClient;
            _logger = logger;

            CreateIndexTemplateAsync().Wait();
        }

        private async Task CreateIndexTemplateAsync()
        {
            var templateRequest = new PutIndexTemplateRequest
            {
                Name = "assetstats_template",
                IndexPatterns = new[] { "assetstats-*" },
                DataStream = new DataStreamVisibility(),
                Template = new IndexTemplateMapping
                {
                    Mappings = new TypeMapping
                    {
                        Properties = new Properties
                        {
                            { "assetId", new LongNumberProperty() },
                            { "protocol", new KeywordProperty() },
                            { "tvlusd", new DoubleNumberProperty() },
                            { "poolCount", new IntegerNumberProperty() },
                            { "lastUpdated", new DateProperty() }
                        }
                    }
                }
            };
            if (_elasticClient == null)
            {
                _logger.LogError("Elasticsearch client is not initialized");
                return;
            }
            var response = await _elasticClient.Indices.PutIndexTemplateAsync(templateRequest);
            _logger.LogInformation("AssetStat index template created: {ok}", response.IsValidResponse);
        }

        public async Task UpsertAsync(AssetStat stat, CancellationToken cancellationToken = default)
        {
            if (stat == null) throw new ArgumentNullException(nameof(stat));

            _cache[(stat.AssetId, stat.Protocol)] = stat;

            try
            {
                if (_elasticClient != null)
                {
                    var response = await _elasticClient.IndexAsync(stat, idx => idx
                        .Index("assetstats")
                        .Id(stat.Id), cancellationToken);

                    if (!response.IsValidResponse)
                    {
                        _logger.LogWarning("Failed to index asset stat {id}: {error}", stat.Id, response.DebugInformation);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store asset stat {id}", stat.Id);
            }
        }

        public async Task UpsertManyAsync(IEnumerable<AssetStat> stats, CancellationToken cancellationToken = default)
        {
            if (stats == null) return;
            foreach (var stat in stats)
            {
                await UpsertAsync(stat, cancellationToken);
            }
        }

        public IEnumerable<AssetStat> GetAllAsync(DEXProtocol? protocol = null, string? sortBy = null, bool desc = true)
        {
            var filtered = _cache.Values.AsEnumerable();
            if (protocol.HasValue)
            {
                filtered = filtered.Where(s => s.Protocol == protocol.Value);
            }

            if (!string.IsNullOrWhiteSpace(sortBy))
            {
                Func<AssetStat, decimal> key = sortBy.Trim().ToLowerInvariant() switch
                {
                    "tvlusd" => s => s.TVLUSD,
                    "volume24husd" => s => s.Volume24hUSD,
                    "volume7dusd" => s => s.Volume7dUSD,
                    "apr24h" => s => s.Apr24h,
                    "apr7d" => s => s.Apr7d,
                    _ => s => s.TVLUSD
                };
                filtered = desc ? filtered.OrderByDescending(key) : filtered.OrderBy(key);
            }

            return filtered.ToList();
        }

        public AssetStat? GetByAssetIdAsync(ulong assetId, DEXProtocol? protocol = null)
        {
            if (_cache.TryGetValue((assetId, protocol), out var stat))
            {
                return stat;
            }
            return null;
        }
    }
}
