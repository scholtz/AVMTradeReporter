using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace AVMTradeReporter.HealthChecks
{
    /// <summary>
    /// Only registered when AppConfiguration.Redis.Enabled is true (see Program.cs), so
    /// IConnectionMultiplexer is guaranteed to be resolvable whenever this check runs.
    /// </summary>
    public class RedisHealthCheck : IHealthCheck
    {
        private readonly IConnectionMultiplexer _connectionMultiplexer;

        public RedisHealthCheck(IConnectionMultiplexer connectionMultiplexer)
        {
            _connectionMultiplexer = connectionMultiplexer;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var db = _connectionMultiplexer.GetDatabase();
                var latency = await db.PingAsync();
                return HealthCheckResult.Healthy($"Redis reachable ({latency.TotalMilliseconds:F0}ms)");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Redis ping failed", ex);
            }
        }
    }
}
