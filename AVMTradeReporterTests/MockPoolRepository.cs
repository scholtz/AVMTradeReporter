using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Processors.Pool;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests
{
    public class MockPoolRepository : IPoolRepository
    {
        private List<AVMTradeReporter.Model.Data.Pool> pools = new List<AVMTradeReporter.Model.Data.Pool>();

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            pools.Clear();
            return Task.CompletedTask;
        }

        public Task<AVMTradeReporter.Model.Data.Pool?> GetPoolAsync(string poolAddress, CancellationToken cancellationToken)
        {
            var pool = pools.FirstOrDefault(p => p.PoolAddress == poolAddress);
            return Task.FromResult<AVMTradeReporter.Model.Data.Pool?>(pool);
        }

        public Task<bool> StorePoolAsync(AVMTradeReporter.Model.Data.Pool pool, CancellationToken cancellationToken)
        {
            var existing = pools.FirstOrDefault(p => p.PoolAddress == pool.PoolAddress);
            if (existing != null)
            {
                pools.Remove(existing);
            }
            pools.Add(pool);
            return Task.FromResult(true);
        }

        public Task UpdatePoolFromTrade(Trade trade, CancellationToken cancellationToken)
        {
            // No-op for mock
            return Task.CompletedTask;
        }

        public Task UpdatePoolFromLiquidity(Liquidity liquidity, CancellationToken cancellationToken)
        {
            // No-op for mock
            return Task.CompletedTask;
        }

        public async Task<List<AVMTradeReporter.Model.Data.Pool>> GetPoolsAsync(ulong? assetIdA, ulong? assetIdB, string? address, DEXProtocol? protocol = null, int size = 100, CancellationToken cancellationToken = default)
        {
            var filteredPools = pools.AsEnumerable();

            if (assetIdA.HasValue && assetIdB.HasValue)
            {
                filteredPools = filteredPools.Where(p => (p.AssetIdA == assetIdA.Value && p.AssetIdB == assetIdB.Value) || (p.AssetIdB == assetIdA.Value && p.AssetIdA == assetIdB.Value));
            }
            if (!string.IsNullOrEmpty(address))
            {
                filteredPools = filteredPools.Where(p => p.PoolAddress == address);
            }

            // Filter by protocol if specified
            if (protocol.HasValue)
            {
                filteredPools = filteredPools.Where(p => p.Protocol == protocol.Value);
            }

            // Sort by timestamp descending and limit size
            filteredPools = filteredPools
                .OrderByDescending(p => p.Timestamp ?? DateTimeOffset.MinValue)
                .Take(size);
            await Task.Delay(1);
            return filteredPools.ToList();
        }

        public Task<int> GetPoolCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(pools.Count);
        }

        public IPoolProcessor? GetPoolProcessor(DEXProtocol protocol)
        {
            return null;
        }
    }
}
