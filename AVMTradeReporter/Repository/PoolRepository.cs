using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Data;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Microsoft.AspNetCore.SignalR;

namespace AVMTradeReporter.Repository
{
    public class PoolRepository : IPoolRepository
    {
        private readonly ElasticsearchClient _elasticClient;
        private readonly ILogger<PoolRepository> _logger;
        private readonly IHubContext<BiatecScanHub> _hubContext;

        public PoolRepository(
            ElasticsearchClient elasticClient,
            ILogger<PoolRepository> logger,
            IHubContext<BiatecScanHub> hubContext
            )
        {
            _elasticClient = elasticClient;
            _logger = logger;
            _hubContext = hubContext;
            CreatePoolIndexTemplateAsync().Wait();
        }

        async Task CreatePoolIndexTemplateAsync()
        {
            var templateRequest = new PutIndexTemplateRequest
            {
                Name = "pools_template",
                IndexPatterns = new[] { "pools-*" },
                DataStream = new DataStreamVisibility(),
                Template = new IndexTemplateMapping
                {
                    Mappings = new TypeMapping
                    {
                        Properties = new Properties
                        {
                            { "poolAddress", new KeywordProperty() },
                            { "poolAppId", new LongNumberProperty() },
                            { "assetIdA", new LongNumberProperty() },
                            { "assetIdB", new LongNumberProperty() },
                            { "assetIdLP", new LongNumberProperty() },
                            { "assetAmountA", new LongNumberProperty() },
                            { "assetAmountB", new LongNumberProperty() },
                            { "assetAmountLP", new LongNumberProperty() },
                            { "a", new LongNumberProperty() },
                            { "b", new LongNumberProperty() },
                            { "l", new LongNumberProperty() },
                            { "protocol", new KeywordProperty() },
                            { "timestamp", new DateProperty() }
                        }
                    }
                }
            };

            var response = await _elasticClient.Indices.PutIndexTemplateAsync(templateRequest);
            Console.WriteLine($"Pool template created: {response.IsValidResponse}");
        }

        public async Task<Pool?> GetPoolAsync(string poolAddress, ulong poolAppId, DEXProtocol protocol, CancellationToken cancellationToken)
        {
            try
            {
                // Create a unique pool identifier combining address, app ID, and protocol
                var poolId = $"{poolAddress}_{poolAppId}_{protocol}";
                
                var response = await _elasticClient.GetAsync<Pool>(poolId, idx => idx.Index("pools"), cancellationToken);
                
                if (response.IsValidResponse && response.Found)
                {
                    return response.Source;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pool {poolAddress}_{poolAppId}_{protocol}", poolAddress, poolAppId, protocol);
                return null;
            }
        }

        public async Task<bool> StorePoolAsync(Pool pool, CancellationToken cancellationToken)
        {
            try
            {
                var poolId = $"{pool.PoolAddress}_{pool.PoolAppId}_{pool.Protocol}";
                
                var response = await _elasticClient.IndexAsync(pool, idx => idx
                    .Index("pools")
                    .Id(poolId), cancellationToken);

                if (response.IsValidResponse)
                {
                    _logger.LogDebug("Pool updated: {poolId}", poolId);
                    
                    // Publish pool update to SignalR hub
                    await PublishPoolUpdateToHub(pool, cancellationToken);
                    
                    return true;
                }
                else
                {
                    _logger.LogError("Failed to store pool {poolId}: {error}", poolId, response.DebugInformation);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store pool");
                return false;
            }
        }

        public async Task UpdatePoolFromTrade(Trade trade, CancellationToken cancellationToken)
        {
            // Only update from confirmed trades
            if (trade.TradeState != TradeState.Confirmed)
            {
                _logger.LogDebug("Skipping pool update from unconfirmed trade {txId}", trade.TxId);
                return;
            }

            try
            {
                var existingPool = await GetPoolAsync(trade.PoolAddress, trade.PoolAppId, trade.Protocol, cancellationToken);
                
                // If pool doesn't exist, create a new one from trade data
                if (existingPool == null)
                {
                    var newPool = CreatePoolFromTrade(trade);
                    await StorePoolAsync(newPool, cancellationToken);
                    _logger.LogInformation("Created new pool from trade: {poolAddress}_{poolAppId}_{protocol}", 
                        trade.PoolAddress, trade.PoolAppId, trade.Protocol);
                    return;
                }

                // Only update if timestamp is equal or larger
                if (trade.Timestamp.HasValue && existingPool.Timestamp.HasValue && 
                    trade.Timestamp.Value < existingPool.Timestamp.Value)
                {
                    _logger.LogDebug("Skipping pool update - trade timestamp {tradeTime} is older than pool timestamp {poolTime}", 
                        trade.Timestamp.Value, existingPool.Timestamp.Value);
                    return;
                }

                // Update pool with trade data
                UpdatePoolWithTradeData(existingPool, trade);
                await StorePoolAsync(existingPool, cancellationToken);
                
                _logger.LogDebug("Updated pool from trade: {poolAddress}_{poolAppId}_{protocol}", 
                    trade.PoolAddress, trade.PoolAppId, trade.Protocol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update pool from trade {txId}", trade.TxId);
            }
        }

        public async Task UpdatePoolFromLiquidity(Liquidity liquidity, CancellationToken cancellationToken)
        {
            // Only update from confirmed liquidity updates
            if (liquidity.TxState != TradeState.Confirmed)
            {
                _logger.LogDebug("Skipping pool update from unconfirmed liquidity {txId}", liquidity.TxId);
                return;
            }

            try
            {
                var existingPool = await GetPoolAsync(liquidity.PoolAddress, liquidity.PoolAppId, liquidity.Protocol, cancellationToken);
                
                // If pool doesn't exist, create a new one from liquidity data
                if (existingPool == null)
                {
                    var newPool = CreatePoolFromLiquidity(liquidity);
                    await StorePoolAsync(newPool, cancellationToken);
                    _logger.LogInformation("Created new pool from liquidity: {poolAddress}_{poolAppId}_{protocol}", 
                        liquidity.PoolAddress, liquidity.PoolAppId, liquidity.Protocol);
                    return;
                }

                // Only update if timestamp is equal or larger
                if (liquidity.Timestamp.HasValue && existingPool.Timestamp.HasValue && 
                    liquidity.Timestamp.Value < existingPool.Timestamp.Value)
                {
                    _logger.LogDebug("Skipping pool update - liquidity timestamp {liquidityTime} is older than pool timestamp {poolTime}", 
                        liquidity.Timestamp.Value, existingPool.Timestamp.Value);
                    return;
                }

                // Update pool with liquidity data
                UpdatePoolWithLiquidityData(existingPool, liquidity);
                await StorePoolAsync(existingPool, cancellationToken);
                
                _logger.LogDebug("Updated pool from liquidity: {poolAddress}_{poolAppId}_{protocol}", 
                    liquidity.PoolAddress, liquidity.PoolAppId, liquidity.Protocol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update pool from liquidity {txId}", liquidity.TxId);
            }
        }

        private Pool CreatePoolFromTrade(Trade trade)
        {
            return new Pool
            {
                PoolAddress = trade.PoolAddress,
                PoolAppId = trade.PoolAppId,
                AssetIdA = trade.AssetIdIn,  // Assuming AssetIdIn is AssetA
                AssetIdB = trade.AssetIdOut, // Assuming AssetIdOut is AssetB
                AssetIdLP = 0,  // Not available in trade data
                AssetAmountA = 0,  // Pool reserves not directly available from trade
                AssetAmountB = 0,  // Pool reserves not directly available from trade
                AssetAmountLP = 0, // Not available in trade data
                A = trade.A,
                B = trade.B,
                L = trade.L,
                Protocol = trade.Protocol,
                Timestamp = trade.Timestamp
            };
        }

        private Pool CreatePoolFromLiquidity(Liquidity liquidity)
        {
            return new Pool
            {
                PoolAddress = liquidity.PoolAddress,
                PoolAppId = liquidity.PoolAppId,
                AssetIdA = liquidity.AssetIdA,
                AssetIdB = liquidity.AssetIdB,
                AssetIdLP = liquidity.AssetIdLP,
                AssetAmountA = liquidity.AssetAmountA,
                AssetAmountB = liquidity.AssetAmountB,
                AssetAmountLP = liquidity.AssetAmountLP,
                A = liquidity.A,
                B = liquidity.B,
                L = liquidity.L,
                Protocol = liquidity.Protocol,
                Timestamp = liquidity.Timestamp
            };
        }

        private void UpdatePoolWithTradeData(Pool pool, Trade trade)
        {
            // Update the pool state with the latest trade information
            pool.A = trade.A;
            pool.B = trade.B;
            pool.L = trade.L;
            pool.Timestamp = trade.Timestamp;
            
            // Update asset IDs if they weren't set before
            if (pool.AssetIdA == 0) pool.AssetIdA = trade.AssetIdIn;
            if (pool.AssetIdB == 0) pool.AssetIdB = trade.AssetIdOut;
        }

        private void UpdatePoolWithLiquidityData(Pool pool, Liquidity liquidity)
        {
            // Update the pool state with the latest liquidity information
            pool.AssetIdA = liquidity.AssetIdA;
            pool.AssetIdB = liquidity.AssetIdB;
            pool.AssetIdLP = liquidity.AssetIdLP;
            pool.AssetAmountA = liquidity.AssetAmountA;
            pool.AssetAmountB = liquidity.AssetAmountB;
            pool.AssetAmountLP = liquidity.AssetAmountLP;
            pool.A = liquidity.A;
            pool.B = liquidity.B;
            pool.L = liquidity.L;
            pool.Timestamp = liquidity.Timestamp;
        }

        private async Task PublishPoolUpdateToHub(Pool pool, CancellationToken cancellationToken)
        {
            try
            {
                await _hubContext.Clients.All.SendAsync("PoolUpdated", pool, cancellationToken);
                _logger.LogDebug("Published pool update to SignalR hub: {poolAddress}_{poolAppId}_{protocol}", 
                    pool.PoolAddress, pool.PoolAppId, pool.Protocol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish pool update to SignalR hub");
            }
        }

        public async Task<List<Pool>> GetPoolsAsync(DEXProtocol? protocol = null, int size = 100, CancellationToken cancellationToken = default)
        {
            try
            {
                // For now, use a simple approach - we can enhance this later with proper search
                // This is a simplified implementation that gets all documents
                var searchResponse = await _elasticClient.SearchAsync<Pool>(s => s
                    .Index("pools")
                    .Size(size), cancellationToken);

                if (searchResponse.IsValidResponse)
                {
                    var pools = searchResponse.Documents.ToList();
                    
                    // Filter by protocol if specified
                    if (protocol.HasValue)
                    {
                        pools = pools.Where(p => p.Protocol == protocol.Value).ToList();
                    }
                    
                    // Sort by timestamp descending
                    pools = pools.OrderByDescending(p => p.Timestamp ?? DateTimeOffset.MinValue).ToList();
                    
                    return pools;
                }

                _logger.LogError("Failed to search pools: {error}", searchResponse.DebugInformation);
                return new List<Pool>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pools");
                return new List<Pool>();
            }
        }
    }
}