using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AVMTradeReporter.HealthChecks
{
    /// <summary>
    /// Pings the Elasticsearch cluster. Elasticsearch is optional in some environments (the
    /// singleton factory in Program.cs returns null when Elastic.Host/ApiKey are unset), so an
    /// absent client reports Healthy rather than failing the whole /health response.
    /// </summary>
    public class ElasticsearchHealthCheck : IHealthCheck
    {
        private readonly ElasticsearchClient? _client;

        public ElasticsearchHealthCheck(ElasticsearchClient client)
        {
            _client = client;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (_client == null)
            {
                return HealthCheckResult.Healthy("Elasticsearch not configured");
            }

            try
            {
                var response = await _client.PingAsync(cancellationToken);
                return response.IsValidResponse
                    ? HealthCheckResult.Healthy("Elasticsearch reachable")
                    : HealthCheckResult.Unhealthy($"Elasticsearch ping failed: {response.DebugInformation}");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Elasticsearch ping threw", ex);
            }
        }
    }
}
