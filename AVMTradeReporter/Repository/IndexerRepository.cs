using AVMTradeReporter.Model.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using StackExchange.Redis;

namespace AVMIndexReporter.Repository
{
    public class IndexerRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<IndexerRepository> _logger;
        public IndexerRepository(
            ElasticsearchClient elasticClient,
            ILogger<IndexerRepository> logger
            )
        {
            _elasticClient = elasticClient;
            _logger = logger;
        }

        public async Task<bool> StoreIndexerAsync(Indexer indexer, CancellationToken cancellationToken)
        {
            try
            {
                if (_elasticClient == null)
                {
                    return false;
                }
                var indexResult = await _elasticClient.IndexAsync(indexer, new Id(indexer.Id), cancellationToken);

                if (!indexResult.IsValidResponse)
                {
                    _logger.LogError("Indexer store failed: {error}", indexResult.DebugInformation);
                }
                else
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to index indexer");
            }
            return false;
        }
        public async Task<Indexer?> GetIndexerAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                if (_elasticClient == null)
                {
                    return null;
                }
                var getResult = await _elasticClient.GetAsync<Indexer>(new Id(id), cancellationToken);
                if (getResult.IsValidResponse && getResult.Found)
                {
                    return getResult.Source;
                }
                else
                {
                    _logger.LogWarning("Indexer not found: {id}", id);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve indexer with ID {id}", id);
                return null;
            }
        }
    }
}
