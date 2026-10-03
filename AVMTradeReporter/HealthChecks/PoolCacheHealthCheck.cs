using AVMTradeReporter.Repository;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AVMTradeReporter.HealthChecks
{
    /// <summary>
    /// Confirms the in-memory pool cache is populated. Program.cs blocks app.Run() on
    /// PoolRepository.InitializeAsync, so an empty cache after startup indicates the cache was
    /// wiped or never hydrated rather than "still loading".
    /// </summary>
    public class PoolCacheHealthCheck : IHealthCheck
    {
        private readonly IPoolRepository _poolRepository;

        public PoolCacheHealthCheck(IPoolRepository poolRepository)
        {
            _poolRepository = poolRepository;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var count = await _poolRepository.GetPoolCountAsync(cancellationToken);
            if (count > 0) return HealthCheckResult.Healthy($"Pool cache populated ({count} pools)");
            // loaded fine, just nothing stored: a network the DEX is not on yet - not a wiped cache
            return _poolRepository.PoolLoadSucceeded
                ? HealthCheckResult.Healthy("Pool cache loaded, no pools stored yet")
                : HealthCheckResult.Degraded("Pool cache empty");
        }
    }
}
