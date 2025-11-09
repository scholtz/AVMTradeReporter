using Algorand.Algod;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Services
{
    public class PoolRefreshBackgroundService : BackgroundService
    {
        private readonly ILogger<PoolRefreshBackgroundService> _logger;
        private readonly IPoolRepository _poolRepository;
        private readonly IDefaultApi _algod;
        private readonly AppConfiguration _appConfig;
        private readonly IServiceProvider _serviceProvider;

        private readonly TimeSpan _refreshInterval;
        private readonly TimeSpan _delayBetweenPools;
        private readonly TimeSpan _initialDelay;

        private DateTime _lastRun = DateTime.MinValue;

        public PoolRefreshBackgroundService(
            ILogger<PoolRefreshBackgroundService> logger,
            IPoolRepository poolRepository,
            IDefaultApi algod,
            IOptions<AppConfiguration> appConfig,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _poolRepository = poolRepository;
            _algod = algod;
            _appConfig = appConfig.Value;
            _serviceProvider = serviceProvider;

            // Configure intervals from settings
            _refreshInterval = TimeSpan.FromHours(_appConfig.PoolRefresh.IntervalHours);
            _delayBetweenPools = TimeSpan.FromSeconds(_appConfig.PoolRefresh.DelayBetweenPoolsSeconds);
            _initialDelay = TimeSpan.FromMinutes(_appConfig.PoolRefresh.InitialDelayMinutes);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Check if the service is enabled
            if (!_appConfig.PoolRefresh.Enabled)
            {
                _logger.LogInformation("Pool Refresh Background Service is disabled via configuration.");
                return;
            }

            _logger.LogInformation("Pool Refresh Background Service starting... Refresh interval: {interval} hours, Delay between pools: {delay} seconds",
                _appConfig.PoolRefresh.IntervalHours, _appConfig.PoolRefresh.DelayBetweenPoolsSeconds);

            // Wait for initial startup
            await Task.Delay(_initialDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;

                    // Check if we should run
                    if (now - _lastRun >= _refreshInterval)
                    {
                        _logger.LogInformation("Starting pool refresh at {time}", now);
                        await RefreshAllPoolsAsync(stoppingToken);
                        _lastRun = now;
                        _logger.LogInformation("Completed pool refresh at {time}", DateTime.UtcNow);
                    }

                    // Wait for 1 hour before checking again
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Pool Refresh Background Service was cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Pool Refresh Background Service");

                    // Wait before retrying
                    await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
                }
            }
        }

        private async Task RefreshAllPoolsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Initialize pool repository if needed
                await _poolRepository.InitializeAsync(cancellationToken);

                // Get all pools from repository
                var allPools = await _poolRepository.GetPoolsAsync(null, null, null, size: int.MaxValue, cancellationToken: cancellationToken);
                _logger.LogInformation("Found {poolCount} pools to refresh", allPools.Count);

                if (allPools.Count == 0)
                {
                    _logger.LogWarning("No pools found to refresh");
                    return;
                }

                int processedCount = 0;
                int errorCount = 0;

                foreach (var pool in allPools)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        await RefreshSinglePoolAsync(pool, cancellationToken);
                        processedCount++;

                        _logger.LogDebug("Refreshed pool {poolAddress} ({protocol}) - {processed}/{total}",
                            pool.PoolAddress, pool.Protocol, processedCount, allPools.Count);
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        _logger.LogWarning(ex, "Failed to refresh pool {poolAddress} ({protocol})",
                            pool.PoolAddress, pool.Protocol);
                    }

                    // Delay between pools (configurable)
                    if (processedCount < allPools.Count) // Don't delay after the last pool
                    {
                        await Task.Delay(_delayBetweenPools, cancellationToken);
                    }
                }

                _logger.LogInformation("Pool refresh completed. Processed: {processed}, Errors: {errors}, Total: {total}",
                    processedCount, errorCount, allPools.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh pools");
                throw;
            }
        }

        private async Task RefreshSinglePoolAsync(Pool pool, CancellationToken cancellationToken)
        {
            IPoolProcessor? processor = null;

            try
            {
                // Get the appropriate processor for this pool's protocol
                processor = GetPoolProcessor(pool.Protocol);
                if (processor == null)
                {
                    _logger.LogWarning("No processor found for protocol {protocol}", pool.Protocol);
                    return;
                }

                // Load fresh pool data from blockchain
                await processor.LoadPoolAsync(pool.PoolAddress, pool.PoolAppId);

                // Store the refreshed pool data
                //await _poolRepository.StorePoolAsync(refreshedPool, cancellationToken);

                _logger.LogDebug("Successfully refreshed pool {poolAddress} with protocol {protocol}",
                    pool.PoolAddress, pool.Protocol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing pool {appId} {poolAddress} ({protocol}): {error}",
                    pool.PoolAppId, pool.PoolAddress, pool.Protocol, ex.Message);

                if (pool.Protocol == DEXProtocol.Tiny)
                {
                    // try with pact
                    try
                    {
                        processor = GetPoolProcessor(DEXProtocol.Pact);
                        if (processor != null)
                        {
                            await processor.LoadPoolAsync(pool.PoolAddress, pool.PoolAppId);
                        }
                    }
                    catch (Exception pactEx)
                    {
                        _logger.LogError(pactEx, "Failed to refresh pool {appId} {poolAddress} with Pact processor: {error}",
                            pool.PoolAppId, pool.PoolAddress, pactEx.Message);
                    }
                }

                if (pool.Protocol == DEXProtocol.Pact)
                {
                    // try with pact
                    try
                    {
                        processor = GetPoolProcessor(DEXProtocol.Tiny);
                        if (processor != null)
                        {
                            await processor.LoadPoolAsync(pool.PoolAddress, pool.PoolAppId);
                        }
                    }
                    catch (Exception pactEx)
                    {
                        _logger.LogError(pactEx, "Failed to refresh pool {appId} {poolAddress} with Tiny processor: {error}",
                            pool.PoolAppId, pool.PoolAddress, pactEx.Message);
                    }
                }
                throw;
            }
        }

        private IPoolProcessor? GetPoolProcessor(DEXProtocol protocol)
        {
            return protocol switch
            {
                DEXProtocol.Pact => _serviceProvider.GetService<PactPoolProcessor>(),
                DEXProtocol.Tiny => _serviceProvider.GetService<TinyPoolProcessor>(),
                DEXProtocol.Biatec => _serviceProvider.GetService<BiatecPoolProcessor>(),
                _ => null
            };
        }
    }
}