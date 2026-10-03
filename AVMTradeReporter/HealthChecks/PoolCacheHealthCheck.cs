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
            return count > 0
                ? HealthCheckResult.Healthy($"Pool cache populated ({count} pools)")
                : HealthCheckResult.Degraded("Pool cache empty");
        }
    }
}
