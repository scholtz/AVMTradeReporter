using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Services
{
    public class VolumeUpdateBackgroundService : BackgroundService
    {
        private readonly ILogger<VolumeUpdateBackgroundService> _logger;
        private readonly IPoolRepository _poolRepository;
        private readonly AppConfiguration _appConfig;

        public VolumeUpdateBackgroundService(
            ILogger<VolumeUpdateBackgroundService> logger,
            IPoolRepository poolRepository,
            IOptions<AppConfiguration> appConfig)
        {
            _logger = logger;
            _poolRepository = poolRepository;
            _appConfig = appConfig.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Check if the service is enabled
            if (!_appConfig.VolumeUpdate?.Enabled ?? true) // default enabled if not configured
            {
                _logger.LogInformation("Volume Update Background Service is disabled via configuration.");
                return;
            }

            var interval = TimeSpan.FromSeconds(_appConfig.VolumeUpdate?.IntervalSeconds ?? 60);

            _logger.LogInformation("Volume Update Background Service starting... Update interval: {interval} seconds", interval.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken);

                    // Get pools that had recent trades
                    var poolsToUpdate = TradeRepository.GetPoolsWithRecentTrades();
                    if (poolsToUpdate.Any())
                    {
                        _logger.LogInformation("Updating volumes for {count} pools that had recent trades", poolsToUpdate.Count());

                        // Update volumes for these pools
                        await UpdateVolumesForPoolsAsync(poolsToUpdate, stoppingToken);

                        // Clear the list
                        TradeRepository.ClearPoolsWithRecentTrades();
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Volume Update Background Service was cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Volume Update Background Service");

                    // Wait before retrying
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }

        private async Task UpdateVolumesForPoolsAsync(IEnumerable<string> poolAddresses, CancellationToken cancellationToken)
        {
            try
            {
                // Update pool volumes
                await ((PoolRepository)_poolRepository).UpdatePoolVolumesAsync(poolAddresses, cancellationToken);

                // Update aggregated pools for affected pairs
                var updatedPools = poolAddresses.Select(addr => _poolRepository.GetPoolAsync(addr, cancellationToken).Result)
                    .Where(p => p != null)
                    .ToList();

                var pairs = updatedPools
                    .Where(p => p!.AssetIdA.HasValue && p!.AssetIdB.HasValue)
                    .Select(p => (p!.AssetIdA!.Value, p!.AssetIdB!.Value))
                    .Distinct();

                foreach (var (aId, bId) in pairs)
                {
                    await _poolRepository.UpdateAggregatedPool(aId, bId, cancellationToken);
                }

                _logger.LogInformation("Updated volumes and aggregated pools for {count} pairs", pairs.Count());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update volumes for pools");
            }
        }
    }
}