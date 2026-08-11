using AVMTradeReporter.Model.Configuration;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Runs <see cref="IOhlcUsdRepairService"/> once shortly after startup and exits. The repair
    /// itself is idempotent and guarded by a per-version Redis marker, so every replica/restart
    /// after the first successful run turns this into a cheap no-op.
    /// </summary>
    public class OhlcUsdRepairBackgroundService : BackgroundService
    {
        private readonly ILogger<OhlcUsdRepairBackgroundService> _logger;
        private readonly IOhlcUsdRepairService _repairService;
        private readonly AppConfiguration _appConfig;

        public OhlcUsdRepairBackgroundService(
            ILogger<OhlcUsdRepairBackgroundService> logger,
            IOhlcUsdRepairService repairService,
            IOptions<AppConfiguration> appConfig)
        {
            _logger = logger;
            _repairService = repairService;
            _appConfig = appConfig.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_appConfig.OhlcRepair?.Enabled == false)
            {
                _logger.LogInformation("OHLC USD repair is disabled via configuration.");
                return;
            }

            // Give Elasticsearch/Redis connections and startup indexing a moment to settle.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var repaired = await _repairService.RepairAsync(stoppingToken);
                if (repaired >= 0)
                {
                    _logger.LogInformation("One-shot OHLC USD repair finished for {count} assets", repaired);
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown during repair — the marker was not written, so the next start retries
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "One-shot OHLC USD repair failed; it will retry on next startup");
            }
        }
    }
}
