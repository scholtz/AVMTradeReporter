using AVMTradeReporter.Model.Configuration;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Recomputes the "top assets" highlight lists every 5 minutes (configurable) and stores them in
    /// Redis, so <see cref="Controllers.TopAssetsController"/> can serve heavy traffic from the cache
    /// instead of computing per request. Follows the same polling/error-handling pattern as
    /// <see cref="AssetStatsBackgroundService"/>.
    /// </summary>
    public class TopAssetsBackgroundService : BackgroundService
    {
        private readonly ILogger<TopAssetsBackgroundService> _logger;
        private readonly ITopAssetsService _topAssetsService;
        private readonly AppConfiguration _appConfig;

        public TopAssetsBackgroundService(
            ILogger<TopAssetsBackgroundService> logger,
            ITopAssetsService topAssetsService,
            IOptions<AppConfiguration> appConfig)
        {
            _logger = logger;
            _topAssetsService = topAssetsService;
            _appConfig = appConfig.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_appConfig.TopAssets?.Enabled ?? true) // default enabled if not configured
            {
                _logger.LogInformation("Top Assets Background Service is disabled via configuration.");
                return;
            }

            var interval = TimeSpan.FromSeconds(Math.Max(60, _appConfig.TopAssets?.IntervalSeconds ?? 300));

            _logger.LogInformation("Top Assets Background Service starting... Update interval: {interval} seconds", interval.TotalSeconds);

            // Short initial delay so asset/pool caches are populated before the first computation.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var response = await _topAssetsService.ComputeAndCacheAsync(stoppingToken);
                    _logger.LogInformation(
                        "Recomputed top assets lists (popular: {popular}, trending: {trending}, gainers: {gainers}, losers: {losers}, value gainers: {valueGainers}, value losers: {valueLosers})",
                        response.Popular.Count, response.Trending.Count, response.TopGainers.Count,
                        response.TopLosers.Count, response.TopValueGainers.Count, response.TopValueLosers.Count);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Top Assets Background Service was cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Top Assets Background Service");
                }

                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Top Assets Background Service was cancelled.");
                    break;
                }
            }
        }
    }
}
