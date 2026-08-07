using AVMTradeReporter.Model.Configuration;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Precomputes the 7d hourly price/TVL OHLC series for the top-TVL asset universe once per hour
    /// (shortly after each hour boundary, when a fresh price candle and TVL snapshot exist) and stores
    /// them in Redis, so <see cref="Controllers.AssetTimeseriesController"/> serves list pages from
    /// cache instead of querying Elasticsearch per request.
    /// </summary>
    public class AssetTimeseriesBackgroundService : BackgroundService
    {
        private readonly ILogger<AssetTimeseriesBackgroundService> _logger;
        private readonly IAssetTimeseriesService _timeseriesService;
        private readonly AppConfiguration _appConfig;

        public AssetTimeseriesBackgroundService(
            ILogger<AssetTimeseriesBackgroundService> logger,
            IAssetTimeseriesService timeseriesService,
            IOptions<AppConfiguration> appConfig)
        {
            _logger = logger;
            _timeseriesService = timeseriesService;
            _appConfig = appConfig.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_appConfig.AssetTimeseries?.Enabled == false)
            {
                _logger.LogInformation("Asset Timeseries Background Service is disabled via configuration.");
                return;
            }

            _logger.LogInformation("Asset Timeseries Background Service starting (hourly refresh)...");

            // Short initial delay so asset/pool caches are populated before the first computation.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var count = await _timeseriesService.ComputeAndCacheUniverseAsync(stoppingToken);
                    _logger.LogInformation("Recomputed 7d timeseries for {count} assets", count);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Asset Timeseries Background Service");
                }

                // Sleep until shortly past the next hour boundary so each refresh picks up the hour's
                // fresh candles/snapshots as soon as they exist.
                var now = DateTimeOffset.UtcNow;
                var nextRun = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero)
                    .AddHours(1)
                    .AddSeconds(90);
                var delay = nextRun - now;
                if (delay < TimeSpan.FromMinutes(1)) delay = TimeSpan.FromMinutes(1);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
