using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.HealthChecks
{
    /// <summary>
    /// Unhealthy once the indexer is stalled inside a live process (see <see cref="IndexerStallMonitor"/>), so the
    /// liveness probe restarts the pod. The restart resumes at <c>Indexer.StoredThrough + 1</c> - the watermark never
    /// passes a block that did not finish - so nothing is lost and only the few blocks since the hang are re-processed.
    /// </summary>
    public class IndexerStallHealthCheck : IHealthCheck
    {
        private readonly IndexerStallMonitor _monitor;
        private readonly IOptions<AppConfiguration> _config;
        private readonly ILogger<IndexerStallHealthCheck> _logger;

        public IndexerStallHealthCheck(IndexerStallMonitor monitor, IOptions<AppConfiguration> config, ILogger<IndexerStallHealthCheck> logger)
        {
            _monitor = monitor;
            _config = config;
            _logger = logger;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var minutes = _config.Value.BlockProcessing.StallRestartMinutes;
            var stall = _monitor.DescribeStall(TimeSpan.FromMinutes(minutes));
            if (stall == null) return Task.FromResult(HealthCheckResult.Healthy("Indexer is making progress"));

            // logged on every probe until the liveness probe restarts the pod (a handful of lines): this is the only trace
            // a hang leaves, and the reason for the restart must be findable in the pod's previous log
            _logger.LogError("Indexer stalled: {stall}. Reporting unhealthy so that the pod is restarted and resumes at the last fully stored block (threshold {minutes} minutes)", stall, minutes);
            return Task.FromResult(HealthCheckResult.Unhealthy($"Indexer stalled: {stall}"));
        }
    }
}
