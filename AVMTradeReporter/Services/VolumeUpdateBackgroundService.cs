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

                    // Atomically drain pools that had a brand new trade since the last tick (fast
                    // path) - the drain itself always runs so no trade is lost even when this tick
                    // ends up doing a full sweep instead.
                    var poolsWithRecentTrades = TradeRepository.DrainPoolsWithRecentTrades();

                    var poolsToUpdate = DeterminePoolsToUpdate(
                        fullSweepDue,
                        poolsWithRecentTrades,
                        fullSweepDue ? _poolRepository.GetAllPoolAddresses() : Array.Empty<string>());

                    var sweepSucceeded = true;
                    if (poolsToUpdate.Any())
                    {
                        _logger.LogInformation(
                            fullSweepDue
                                ? "Running full volume sweep for {count} pools"
                                : "Updating volumes for {count} pools that had recent trades",
                            poolsToUpdate.Count);

                        sweepSucceeded = await UpdateVolumesForPoolsAsync(poolsToUpdate, stoppingToken);
                    }

                    if (ShouldAdvanceFullSweepClock(fullSweepDue, sweepSucceeded))
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
        /// Whether this tick's full-sweep clock should advance. A due sweep that failed (e.g. a
        /// transient Elasticsearch error) must NOT be treated as completed - otherwise a quiet pool
        /// would go uncorrected for up to another whole <c>FullSweepIntervalSeconds</c> on top of the
        /// interval that just failed. Pure/testable.
        /// </summary>
        internal static bool ShouldAdvanceFullSweepClock(bool fullSweepDue, bool sweepSucceeded)
        {
            return fullSweepDue && sweepSucceeded;
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

        /// <returns>false if the sweep hit an error and did not complete; the caller must not treat
        /// this tick as done (see the full-sweep clock in <see cref="ExecuteAsync"/>).</returns>
        private async Task<bool> UpdateVolumesForPoolsAsync(IEnumerable<string> poolAddresses, CancellationToken cancellationToken)
        {
            try
            {
                var poolAddressList = poolAddresses as ICollection<string> ?? poolAddresses.ToList();

                // Update pool volumes
                await ((PoolRepository)_poolRepository).UpdatePoolVolumesAsync(poolAddressList, cancellationToken);

                // Update aggregated pools for affected pairs. Fetched concurrently rather than one
                // blocking GetPoolAsync().Result per pool - sequentially blocking on potentially
                // thousands of pools every full-sweep tick stalls this service's own loop (delaying
                // the fast incremental path for actively-trading pools too).
                var updatedPools = await Task.WhenAll(poolAddressList.Select(addr => _poolRepository.GetPoolAsync(addr, cancellationToken)));

                var pairs = updatedPools
                    .Where(p => p != null && p.AssetIdA.HasValue && p.AssetIdB.HasValue)
                    .Select(p => (p!.AssetIdA!.Value, p!.AssetIdB!.Value))
                    .Distinct()
                    .ToList();

                foreach (var (aId, bId) in pairs)
                {
                    try
                    {
                        await _poolRepository.UpdateAggregatedPool(aId, bId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        // One bad pair (e.g. malformed asset data) must not stop the rest of the
                        // sweep - especially now that a full sweep can cover every pair in the system.
                        _logger.LogError(ex, "Failed to update aggregated pool for pair {aId}/{bId}", aId, bId);
                    }
                }

                _logger.LogInformation("Updated volumes and aggregated pools for {count} pairs", pairs.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update volumes for pools");
                return false;
            }
        }
    }
}
