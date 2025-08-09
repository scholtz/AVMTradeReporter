using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Repository;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests
{
    public class MockPoolRepository : IPoolRepository
    {
        private List<Pool> pools = new List<Pool>();

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            pools.Clear();
            return Task.CompletedTask;
        }

        public Task<Pool?> GetPoolAsync(string poolAddress, CancellationToken cancellationToken)
        {
            var pool = pools.FirstOrDefault(p => p.PoolAddress == poolAddress);
            return Task.FromResult<Pool?>(pool);
        }

        public Task<bool> StorePoolAsync(Pool pool, CancellationToken cancellationToken)
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

        public Task<List<Pool>> GetPoolsAsync(DEXProtocol? protocol = null, int size = 100, CancellationToken cancellationToken = default)
        {
            var result = protocol.HasValue
                ? pools.Where(p => p.Protocol == protocol.Value).Take(size).ToList()
                : pools.Take(size).ToList();
            return Task.FromResult(result);
        }

        public Task<int> GetPoolCountAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(pools.Count);
        }
    }
}
