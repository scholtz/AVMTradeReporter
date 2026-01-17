using Algorand;
using Algorand.Algod;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using AVMIndexReporter.Repository;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.Valuation;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace AVMTradeReporter.Services
{
    public class TradeReporterBackgroundService : BackgroundService, ITradeService, ILiquidityService
    {
        private readonly ILogger<TradeReporterBackgroundService> _logger;
        private readonly IOptions<AppConfiguration> _appConfig;
        private readonly Algorand.Algod.IDefaultApi _algod;
        private readonly Algorand.Algod.IDefaultApi? _algod2;
        private readonly Algorand.Algod.IDefaultApi? _algod3;
        private readonly HttpClient _httpClient;
        private readonly HttpClient? _httpClient2;
        private readonly HttpClient? _httpClient3;
        private readonly IndexerRepository _indexerRepository;
        private readonly TradeRepository _tradeRepository;
        private readonly LiquidityRepository _liquidityRepository;
        private readonly IPoolRepository _poolRepository;
        private readonly IAssetRepository _assetRepository;
        private readonly TransactionProcessor _transactionProcessor;
        private readonly BlockRepository _blockRepository;

        // Async processing support
        private readonly SemaphoreSlim _concurrentTasksSemaphore;
        private readonly List<Task> _runningTasks = new List<Task>();
        private readonly object _tasksLock = new object();
        private DateTime _lastMemoryCheck = DateTime.MinValue;


        public static Indexer? Indexer { get; set; }

        public TradeReporterBackgroundService(
            ILogger<TradeReporterBackgroundService> logger,
            IOptions<AppConfiguration> appConfig,
            IndexerRepository indexerRepository,
            TradeRepository tradeRepository,
            LiquidityRepository liquidityRepository,
            IPoolRepository poolRepository,
            IAssetRepository assetRepository,
            TransactionProcessor transactionProcessor,
            BlockRepository blockRepository
            )
        {
            _logger = logger;
            _appConfig = appConfig;
            _indexerRepository = indexerRepository;
            _tradeRepository = tradeRepository;
            _liquidityRepository = liquidityRepository;
            _poolRepository = poolRepository;
            _assetRepository = assetRepository;
            _transactionProcessor = transactionProcessor;
            _blockRepository = blockRepository;

            // Initialize semaphore for concurrent task management
            _concurrentTasksSemaphore = new SemaphoreSlim(_appConfig.Value.BlockProcessing.MaxConcurrentTasks, _appConfig.Value.BlockProcessing.MaxConcurrentTasks);

            _httpClient = HttpClientConfigurator.ConfigureHttpClient(appConfig.Value.Algod.Host, appConfig.Value.Algod.ApiKey, appConfig.Value.Algod.Header);
            _algod = new DefaultApi(_httpClient);

            if (!string.IsNullOrEmpty(appConfig.Value.Algod2?.Host))
            {
                _httpClient2 = HttpClientConfigurator.ConfigureHttpClient(appConfig.Value.Algod2.Host, appConfig.Value.Algod2.ApiKey, appConfig.Value.Algod2.Header);
                _algod2 = new DefaultApi(_httpClient2);
            }
            if (!string.IsNullOrEmpty(appConfig.Value.Algod3?.Host))
            {
                _httpClient3 = HttpClientConfigurator.ConfigureHttpClient(appConfig.Value.Algod3.Host, appConfig.Value.Algod3.ApiKey, appConfig.Value.Algod3.Header);
                _algod3 = new DefaultApi(_httpClient3);
            }


#if DEBUG
            Indexer = new Indexer()
            {
                Id = _appConfig.Value.IndexerId,
                Updated = DateTimeOffset.Now,
                Round = _appConfig.Value.StartRound ?? 52337928,
                GenesisId = "mainnet-v1.0"
            };
#else
            var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Indexer = _indexerRepository.GetIndexerAsync(_appConfig.Value.IndexerId, cancellationTokenSource.Token).Result;
            if (Indexer == null)
            {
                // create new indexer
                Indexer = new Indexer()
                {
                    Id = _appConfig.Value.IndexerId,
                    Updated = DateTimeOffset.Now,
                    Round = _appConfig.Value.StartRound ?? 52337928,
                    GenesisId = "mainnet-v1.0"
                };
                var success = indexerRepository.StoreIndexerAsync(Indexer, cancellationTokenSource.Token).Result;
                if (success)
                {
                    _logger.LogInformation("Indexer created with ID: {indexerId}", Indexer.Id);
                }
                else
                {
                    _logger.LogError("Failed to create indexer");
                }
            }
#endif
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Trade Reporter Background Service starting...");

            // Initialize PoolRepository first
            try
            {
                _logger.LogInformation("Initializing PoolRepository...");
                await _poolRepository.InitializeAsync(stoppingToken);
                var poolCount = await _poolRepository.GetPoolCountAsync(stoppingToken);
                _logger.LogInformation("PoolRepository initialized successfully with {poolCount} pools", poolCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize PoolRepository. Continuing without pool cache.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (Indexer == null)
                    {
                        await Task.Delay(1000);
                        continue;
                    }
                    //await ProcessBlockWorkAsync(52243617, stoppingToken);// pact
                    //await ProcessBlockWorkAsync(52279620, stoppingToken);// biatec
                    //await ProcessBlockWorkAsync(52335125, stoppingToken);//tiny

                    if (_appConfig.Value.MinRound != null && _appConfig.Value.MinRound > Indexer.Round)
                    {
                        _logger.LogInformation("Min round reached");
                        await Task.Delay(TimeSpan.FromMinutes(10));
                        continue;
                    }
                    if (_appConfig.Value.MaxRound != null && _appConfig.Value.MaxRound < Indexer.Round)
                    {
                        _logger.LogInformation("Max round reached");
                        await Task.Delay(TimeSpan.FromMinutes(10));
                        continue;
                    }

#if !DEBUG
                    try
                    {
                        var blockStatus = await _algod.WaitForBlockAsync(stoppingToken, Indexer?.Round ?? throw new Exception("Rund not defined"));
                    }
                    catch
                    {
                        if (_algod2 != null)
                        {
                            try
                            {

                                var blockStatus = await _algod2.WaitForBlockAsync(stoppingToken, Indexer?.Round ?? throw new Exception("Rund not defined"));
                            }
                            catch
                            {
                                if (_algod3 != null)
                                {
                                    _logger.LogWarning("Algod2 failed, trying Algod3");
                                    // Try algod3
                                    var blockStatus = await _algod3.WaitForBlockAsync(stoppingToken, Indexer?.Round ?? throw new Exception("Rund not defined"));
                                }
                            }
                        }
                    }
#endif
                    // Clean up completed tasks
                    CleanupCompletedTasks();

                    // Determine if we should process asynchronously
                    bool useAsyncProcessing = _appConfig.Value.BlockProcessing.EnableAsyncProcessing &&
                                            !IsMemoryPressureHigh();

                    if (useAsyncProcessing)
                    {
                        // Try to acquire semaphore without blocking
                        if (_concurrentTasksSemaphore.CurrentCount > 0)
                        {
                            // Start async processing
                            var blockTask = ProcessBlockAsyncWrapper(Indexer.Round, stoppingToken);
                            lock (_tasksLock)
                            {
                                _runningTasks.Add(blockTask);
                            }
                            _logger.LogDebug("Started async processing for block {blockId}. Running tasks: {taskCount}",
                                Indexer.Round, _runningTasks.Count);
                        }
                        else
                        {
                            _logger.LogDebug("Max concurrent tasks reached, processing block {blockId} synchronously", Indexer.Round);
                            await ProcessBlockWorkAsync(Indexer.Round, stoppingToken);
                        }
                    }
                    else
                    {
                        // Process synchronously
                        _logger.LogDebug("Processing block {blockId} synchronously (async disabled or memory pressure)", Indexer.Round);
                        await ProcessBlockWorkAsync(Indexer.Round, stoppingToken);
                    }

                    await IncrementIndexer(stoppingToken);

                    if (_appConfig.Value.DelayMs.HasValue && _appConfig.Value.DelayMs > 0)
                    {
                        await Task.Delay(_appConfig.Value.DelayMs.Value);
                    }
#if DEBUG
                    return;
#endif
                    // 
                    //await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Run every 5 minutes
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Trade Reporter Background Service was cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred in Trade Reporter Background Service.");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Wait before retrying
                }
            }

            _logger.LogInformation("Trade Reporter Background Service stopped.");
        }



        ConcurrentDictionary<string, Trade> _trades = new ConcurrentDictionary<string, Trade>();
        ConcurrentDictionary<string, Liquidity> _liquidityUpdates = new ConcurrentDictionary<string, Liquidity>();

        private async Task RegisterTrade(Trade trade, CancellationToken cancellationToken)
        {

            await PopulateTradeUsdAsync(trade, cancellationToken);
            _trades[trade.TxId] = trade;
        }

        public async Task RegisterLiquidity(Liquidity liquidityUpdate, CancellationToken cancellationToken)
        {
            await PopulateLiquidityUsdAsync(liquidityUpdate, cancellationToken);
            _liquidityUpdates[liquidityUpdate.TxId] = liquidityUpdate;
        }

        private async Task PopulateTradeUsdAsync(Trade trade, CancellationToken cancellationToken)
        {
            if (trade == null) return;

            var assetIn = await _assetRepository.GetAssetAsync(trade.AssetIdIn, cancellationToken);
            var assetOut = await _assetRepository.GetAssetAsync(trade.AssetIdOut, cancellationToken);

            var inUsd = UsdValuation.TryComputeUsdValue(trade.AssetAmountIn, assetIn);
            var outUsd = UsdValuation.TryComputeUsdValue(trade.AssetAmountOut, assetOut);

            trade.ValueUSD = CombineSides(inUsd, outUsd);
            trade.PriceUSD = UsdValuation.TryComputeUsdTradePrice(trade.ValueUSD, trade.AssetAmountOut, assetOut);

            // Fees are always calculated from the input side:
            // gross fee (USD) = (input amount in USD) * LPFee.
            // split: protocol fee = gross fee * ProtocolFeePortion, provider fee = gross fee - protocol fee.
            trade.FeesUSD = null;
            trade.FeesUSDProtocol = null;
            trade.FeesUSDProvider = null;

            var pool = await _poolRepository.GetPoolAsync(trade.PoolAddress, cancellationToken);
            var poolLpFee = pool?.LPFee;
            if (poolLpFee.HasValue && poolLpFee.Value > 0)
            {
                var inputUsd = UsdValuation.TryComputeUsdValue(trade.AssetAmountIn, assetIn);
                if (inputUsd.HasValue)
                {
                    var grossFeeUsd = inputUsd.Value * poolLpFee.Value;
                    trade.FeesUSD = grossFeeUsd;

                    var portion = pool?.ProtocolFeePortion ?? 0m;
                    if (portion < 0) portion = 0m;
                    if (portion > 1) portion = 1m;

                    var protocolFeeUsd = grossFeeUsd * portion;
                    trade.FeesUSDProtocol = protocolFeeUsd;
                    trade.FeesUSDProvider = grossFeeUsd - protocolFeeUsd;
                }
            }
        }

        private async Task PopulateLiquidityUsdAsync(Liquidity liquidity, CancellationToken cancellationToken)
        {
            if (liquidity == null) return;

            var assetA = await _assetRepository.GetAssetAsync(liquidity.AssetIdA, cancellationToken);
            var assetB = await _assetRepository.GetAssetAsync(liquidity.AssetIdB, cancellationToken);

            var aUsd = UsdValuation.TryComputeUsdValue(liquidity.AssetAmountA, assetA);
            var bUsd = UsdValuation.TryComputeUsdValue(liquidity.AssetAmountB, assetB);
            liquidity.ValueUSD = CombineSides(aUsd, bUsd);
        }

        private static decimal? CombineSides(decimal? aUsd, decimal? bUsd)
        {
            if (aUsd.HasValue && bUsd.HasValue) return (aUsd.Value + bUsd.Value) / 2m;
            return aUsd ?? bUsd;
        }

        private static decimal? SumNullable(decimal? a, decimal? b)
        {
            if (a == null && b == null) return null;
            return (a ?? 0m) + (b ?? 0m);
        }

        private bool IsMemoryPressureHigh()
        {
            var now = DateTime.Now;
            if ((now - _lastMemoryCheck).TotalMilliseconds < _appConfig.Value.BlockProcessing.MemoryCheckIntervalMs)
            {
                // Don't check memory too frequently, return false to allow processing
                return false;
            }

            _lastMemoryCheck = now;

            try
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                var memoryUsageMB = process.WorkingSet64 / 1024 / 1024;
                
                if (memoryUsageMB > _appConfig.Value.BlockProcessing.MemoryThresholdMB)
                {
                    _logger.LogWarning("High memory usage detected: {memoryUsageMB} MB (threshold: {threshold} MB). Disabling async processing temporarily.",
                        memoryUsageMB, _appConfig.Value.BlockProcessing.MemoryThresholdMB);
                    return true;
                }

                _logger.LogDebug("Memory usage: {memoryUsageMB} MB (threshold: {threshold} MB)", 
                    memoryUsageMB, _appConfig.Value.BlockProcessing.MemoryThresholdMB);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check memory usage, assuming normal memory pressure");
                return false;
            }
        }

        private void CleanupCompletedTasks()
        {
            lock (_tasksLock)
            {
                _runningTasks.RemoveAll(task => task.IsCompleted);
            }
        }

        private async Task IncrementIndexer(CancellationToken cancellationToken)
        {
#if DEBUG
            await Task.Delay(1);
            return;
#else
            if (Indexer == null) return;
            if (_appConfig.Value.Direction == "-")
            {
                Indexer.Round = Indexer.Round - 1;
            }
            else
            {
                Indexer.Round = Indexer.Round + 1;
            }
            Indexer.Updated = DateTimeOffset.Now;
            await _indexerRepository.StoreIndexerAsync(Indexer, cancellationToken);
#endif
        }
        private async Task ProcessBlockWorkAsync(ulong blockId, CancellationToken cancellationToken)
        {
            try
            {
                var algodConfig = _appConfig.Value.Algod;

                _logger.LogInformation("Loading block {blockId}", blockId);
                CertifiedBlock? block = null;
                try
                {
                    block = await _algod.GetBlockAsync(blockId, Format.Msgpack, false);
                }
                catch
                {
                    if (_algod2 != null)
                    {
                        _logger.LogWarning("Algod failed, trying Algod2");
                        try
                        {
                            block = await _algod2.GetBlockAsync(blockId, Format.Msgpack, false);
                        }
                        catch
                        {
                            if (_algod3 != null)
                            {
                                _logger.LogWarning("Algod2 failed, trying Algod3");
                                block = await _algod3.GetBlockAsync(blockId, Format.Msgpack, false);
                            }
                        }
                    }
                }
                if (block == null || block.Block == null)
                {
                    _logger.LogWarning("Block {blockId} not found", blockId);
                    return;
                }

                _logger.LogInformation("Found transactions: {txCount}", block.Block?.Transactions?.Count);
                await _transactionProcessor.ProcessBlock(block, this, this, cancellationToken);

                var result = await _tradeRepository.StoreTradesAsync(_trades.Values.ToArray(), cancellationToken);
                if (result)
                {
                    _trades.Clear();
                }
                result = await _liquidityRepository.StoreLiquidityUpdatesAsync(_liquidityUpdates.Values.ToArray(), cancellationToken);
                if (result)
                {
                    _liquidityUpdates.Clear();
                }


                if (block.Block != null)
                {
                    await _blockRepository.PublishToHub(Model.Data.Block.FromAlgorandBlock(block.Block), cancellationToken);
                }

                await Task.CompletedTask; // Placeholder for actual work
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Trade Reporter Background Service is stopping...");

            // Wait for all running tasks to complete or timeout after 30 seconds
            try
            {
                List<Task> tasksToWait;
                lock (_tasksLock)
                {
                    tasksToWait = new List<Task>(_runningTasks);
                }

                if (tasksToWait.Count > 0)
                {
                    _logger.LogInformation("Waiting for {taskCount} running block processing tasks to complete...", tasksToWait.Count);
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);
                    
                    try
                    {
                        await Task.WhenAll(tasksToWait).WaitAsync(combinedCts.Token);
                        _logger.LogInformation("All block processing tasks completed successfully");
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("Some block processing tasks did not complete within timeout");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while stopping background service");
            }

            _concurrentTasksSemaphore?.Dispose();
            await base.StopAsync(stoppingToken);
        }

        private async Task ProcessBlockAsyncWrapper(ulong blockId, CancellationToken cancellationToken)
        {
            bool semaphoreAcquired = false;
            try
            {
                // Acquire semaphore to limit concurrent tasks
                await _concurrentTasksSemaphore.WaitAsync(cancellationToken);
                semaphoreAcquired = true;

                _logger.LogDebug("Processing block {blockId} asynchronously", blockId);
                await ProcessBlockWorkAsync(blockId, cancellationToken);
                _logger.LogDebug("Completed async processing for block {blockId}", blockId);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Async processing for block {blockId} was cancelled", blockId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in async processing for block {blockId}", blockId);
            }
            finally
            {
                if (semaphoreAcquired)
                {
                    _concurrentTasksSemaphore.Release();
                }
            }
        }

        Task ITradeService.RegisterTrade(Trade trade, CancellationToken cancellationToken)
        {
            return RegisterTrade(trade, cancellationToken);
        }

    }
}