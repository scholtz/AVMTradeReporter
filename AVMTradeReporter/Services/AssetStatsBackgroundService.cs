using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Periodically recomputes per-asset TVL/volume/fees/APR rollups (<see cref="AVMTradeReporter.Models.Data.AssetStat"/>)
    /// and pushes the results to SignalR subscribers, following the same polling/error-handling pattern as
    /// <see cref="VolumeUpdateBackgroundService"/>.
    /// </summary>
    public class AssetStatsBackgroundService : BackgroundService
    {
        private readonly ILogger<AssetStatsBackgroundService> _logger;
        private readonly IAssetStatsService _assetStatsService;
        private readonly IHubContext<BiatecScanHub> _hubContext;
        private readonly AppConfiguration _appConfig;

        public AssetStatsBackgroundService(
            ILogger<AssetStatsBackgroundService> logger,
            IAssetStatsService assetStatsService,
            IHubContext<BiatecScanHub> hubContext,
            IOptions<AppConfiguration> appConfig)
        {
            _logger = logger;
            _assetStatsService = assetStatsService;
            _hubContext = hubContext;
            _appConfig = appConfig.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_appConfig.AssetStats?.Enabled ?? true) // default enabled if not configured
            {
                _logger.LogInformation("Asset Stats Background Service is disabled via configuration.");
                return;
            }

            var interval = TimeSpan.FromSeconds(_appConfig.AssetStats?.IntervalSeconds ?? 120);

            _logger.LogInformation("Asset Stats Background Service starting... Update interval: {interval} seconds", interval.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var updated = await _assetStatsService.ComputeAssetStatsAsync(stoppingToken);
                    _logger.LogInformation("Computed {count} asset stat rows", updated.Count);

                    foreach (var stat in updated)
                    {
                        await BiatecScanHub.PublishAssetStatAsync(_hubContext, stat, stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Asset Stats Background Service was cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Asset Stats Background Service");
                }

                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Asset Stats Background Service was cancelled.");
                    break;
                }
            }
        }
    }
}
