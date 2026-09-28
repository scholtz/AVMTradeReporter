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
            var fullSweepInterval = TimeSpan.FromSeconds(_appConfig.VolumeUpdate?.FullSweepIntervalSeconds ?? 900);

            _logger.LogInformation(
                "Volume Update Background Service starting... Update interval: {interval} seconds, full sweep interval: {fullSweepInterval} seconds",
                interval.TotalSeconds, fullSweepInterval.TotalSeconds);

            // PoolRepository.InitializeAsync already ran a full sweep at startup, so the first tick
            // only needs to cover the fast incremental path until fullSweepInterval has elapsed.
            var lastFullSweepUtc = DateTimeOffset.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken);

                    var now = DateTimeOffset.UtcNow;
                    var fullSweepDue = IsFullSweepDue(now, lastFullSweepUtc, fullSweepInterval);

                    // Pools that had a brand new trade since the last tick (fast path).
                    var poolsWithRecentTrades = TradeRepository.GetPoolsWithRecentTrades();

                    var poolsToUpdate = DeterminePoolsToUpdate(
                        fullSweepDue,
                        poolsWithRecentTrades,
                        fullSweepDue ? _poolRepository.GetAllPoolAddresses() : Array.Empty<string>());

                    if (poolsToUpdate.Any())
                    {
                        _logger.LogInformation(
                            fullSweepDue
                                ? "Running full volume sweep for {count} pools"
                                : "Updating volumes for {count} pools that had recent trades",
                            poolsToUpdate.Count);

                        await UpdateVolumesForPoolsAsync(poolsToUpdate, stoppingToken);

                        // Clear the list of recently-traded pools regardless of path - a full sweep
                        // already covers them too.
                        TradeRepository.ClearPoolsWithRecentTrades();
                    }

                    if (fullSweepDue)
                    {
                        lastFullSweepUtc = now;
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

        /// <summary>
        /// Whether enough time has passed since the last full sweep across every cached pool.
        /// Pure/testable - no dependency on the running service.
        /// </summary>
        internal static bool IsFullSweepDue(DateTimeOffset now, DateTimeOffset lastFullSweepUtc, TimeSpan fullSweepInterval)
        {
            return now - lastFullSweepUtc >= fullSweepInterval;
        }

        /// <summary>
        /// Decides which pool addresses this tick should refresh. When a full sweep is due, every
        /// cached pool is included (so a pool that has gone quiet still gets its stale volume
        /// corrected - see <c>PoolVolumeStaleResetTests</c> for why that correction only happens when
        /// the pool is actually included in the request). Otherwise only pools with brand new trades
        /// are refreshed, keeping the common-case tick cheap. Pure/testable.
        /// </summary>
        internal static List<string> DeterminePoolsToUpdate(
            bool fullSweepDue,
            IEnumerable<string> poolsWithRecentTrades,
            IEnumerable<string> allPoolAddresses)
        {
            return (fullSweepDue ? allPoolAddresses.Union(poolsWithRecentTrades) : poolsWithRecentTrades)
                .Distinct()
                .ToList();
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
