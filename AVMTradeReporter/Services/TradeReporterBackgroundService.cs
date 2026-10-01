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
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services.CoinGecko;
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
        private readonly IIndexedBlockTracker? _blockTracker;

        // Not-yet-stored documents, per block: a block counts as indexed (latest-block watermark) only once ITS documents
        // are acknowledged by Elasticsearch - see PendingBlockBatches.
        private readonly PendingBlockBatches _pending = new();

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
            BlockRepository blockRepository,
            IIndexedBlockTracker? blockTracker = null
            )
        {
            _blockTracker = blockTracker;
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
                GenesisId = _appConfig.Value.GenesisId
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
                    GenesisId = _appConfig.Value.GenesisId
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

            await SeedBlockTrackerAsync(stoppingToken);

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
                    var waited = false;
                    foreach (var (name, api) in Algods())
                    {
                        try
                        {
                            await api.WaitForBlockAsync(stoppingToken, Indexer?.Round ?? throw new Exception("Rund not defined"));
                            waited = true;
                            break;
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            // only a real stop; an HttpClient timeout is a TaskCanceledException too and must fall through
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "{algod} failed waiting for round {round}, trying the next one", name, Indexer?.Round);
                        }
                    }
                    if (!waited)
                    {
                        // Every algod failed - an outage or a flaky proxy. A short pause with the round unchanged, instead of
                        // fetching (and possibly giving up on) a block that may not exist yet.
                        _logger.LogWarning("No algod could wait for round {round}; retrying shortly", Indexer?.Round);
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
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



        private async Task RegisterTrade(Trade trade, CancellationToken cancellationToken)
        {
            await PopulateTradeUsdAsync(trade, cancellationToken);
            _pending.Add(trade);
        }

        public async Task RegisterLiquidity(Liquidity liquidityUpdate, CancellationToken cancellationToken)
        {
            await PopulateLiquidityUsdAsync(liquidityUpdate, cancellationToken);
            _pending.Add(liquidityUpdate);
        }

        private async Task PopulateTradeUsdAsync(Trade trade, CancellationToken cancellationToken)
        {
            if (trade == null) return;

            var assetIn = await _assetRepository.GetAssetAsync(trade.AssetIdIn, cancellationToken);
            var assetOut = await _assetRepository.GetAssetAsync(trade.AssetIdOut, cancellationToken);

            var inUsd = UsdValuation.TryComputeUsdValue(trade.AssetAmountIn, assetIn);
            var outUsd = UsdValuation.TryComputeUsdValue(trade.AssetAmountOut, assetOut);

            trade.ValueUSD = CombineSides(inUsd, outUsd);

            trade.PriceAssetInUSD = null;
            trade.PriceAssetOutUSD = null;

            if (trade.ValueUSD.HasValue)
            {
                trade.PriceAssetInUSD = UsdValuation.TryComputeUsdTradePrice(trade.ValueUSD, trade.AssetAmountIn, assetIn);
                trade.PriceAssetOutUSD = UsdValuation.TryComputeUsdTradePrice(trade.ValueUSD, trade.AssetAmountOut, assetOut);
            }

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
            RecordStoredThrough();
            await _indexerRepository.StoreIndexerAsync(Indexer, cancellationToken);
#endif
        }

        /// <summary>Copies the contiguous completed watermark onto the indexer document (see <see cref="Indexer.StoredThrough"/>).</summary>
        private void RecordStoredThrough()
        {
            if (Indexer != null && _blockTracker?.CompletedThrough is { } completed) Indexer.StoredThrough = completed.Round;
        }
        /// <summary>
        /// Starts the GeckoTerminal latest-block watermark: every block below the indexer's current round was handled
        /// by an earlier run. Only the forward indexer owns the watermark (a backward / backfill indexer never does).
        /// </summary>
        private async Task SeedBlockTrackerAsync(CancellationToken cancellationToken)
        {
            if (_blockTracker == null || Indexer == null || Indexer.Round == 0) return;
            if (_appConfig.Value.Direction == "-") return;
            try
            {
                // Indexer.Round is persisted when a block task STARTS, so after a crash it sits above blocks whose trades were
                // never stored. Indexer.StoredThrough (persisted with every increment and on shutdown) is where the data really
                // ends: the watermark starts there and the indexer re-processes the gap (stores are idempotent upserts by tx id).
                // A deliberate forward jump of Round must clear StoredThrough on the indexer document as well.
                // The Redis mirror is written only after contiguous completion, so it is as trustworthy as StoredThrough and
                // may even be ahead of it (blocks that completed after the last increment persisted StoredThrough).
                var mirrored = await _blockTracker.GetLatestAsync(cancellationToken);
                var (seedRound, rewind, verified) = ResolveStartupSeed(Indexer.Round, Indexer.StoredThrough, mirrored?.Round);
                if (rewind)
                {
                    _logger.LogWarning("Indexer round {round} is ahead of the last fully stored block {storedThrough} (blocks were in flight when the previous run stopped) - re-processing {count} blocks",
                        Indexer.Round, seedRound, Indexer.Round - 1 - seedRound);
                    Indexer.Round = seedRound + 1;
                    Indexer.Updated = DateTimeOffset.Now;
                    await _indexerRepository.StoreIndexerAsync(Indexer, cancellationToken);
                }

                // Without the real block time the seed is covered but not advertised (latest-block must never carry a made-up
                // timestamp); the next completed block advertises itself. An unverified seed (an old indexer document without
                // StoredThrough and no Redis mirror) is never mirrored either: it would be trusted on the next restart.
                var header = await TryFetchBlockAsync(seedRound, headerOnly: true);
                long? timestamp = header?.Block?.Timestamp != null ? Convert.ToInt64(header.Block.Timestamp) : null;
                if (timestamp == null) _logger.LogWarning("Could not read the header of block {round}: the latest-block watermark starts unadvertised until the next block completes", seedRound);
                _blockTracker.Seed(seedRound, timestamp, verified);
                _logger.LogInformation("Latest indexed block watermark starts at {round}", seedRound);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed the latest indexed block watermark");
            }
        }

        private const int BlockFetchAttempts = 4;
        private const int BlockProcessAttempts = 3;

        /// <summary>
        /// Where the latest-block watermark (and, when it has to, the indexer) restarts. The persisted <c>StoredThrough</c>
        /// is the truth; the Redis mirror may raise it (blocks that completed after the last increment persisted it) but
        /// never replaces it: without a persisted value there is no rewind at all - an operator who jumps <c>Round</c>
        /// forward clears <c>StoredThrough</c>, and a stale mirror must not undo that. Such a seed is <c>Verified = false</c>:
        /// advertised, but never mirrored to Redis nor persisted as <c>StoredThrough</c> until this run completed a block.
        /// Neither source may seed above the round before the indexer's: an operator who moves <c>Round</c> back for a
        /// re-index must not have the stale mirror advertise blocks that are being rewritten.
        /// </summary>
        internal static (ulong SeedRound, bool Rewind, bool Verified) ResolveStartupSeed(ulong indexerRound, ulong? storedThrough, ulong? mirroredRound)
        {
            var seedRound = indexerRound - 1;
            if (storedThrough is not { } persisted) return (seedRound, false, false);
            var known = Math.Min(mirroredRound.HasValue ? Math.Max(persisted, mirroredRound.Value) : persisted, seedRound);
            return known < seedRound ? (known, true, true) : (seedRound, false, true);
        }

        private IEnumerable<(string Name, Algorand.Algod.IDefaultApi Api)> Algods()
        {
            yield return ("Algod", _algod);
            if (_algod2 != null) yield return ("Algod2", _algod2);
            if (_algod3 != null) yield return ("Algod3", _algod3);
        }

        /// <summary>Loads a block from the first algod that answers (primary, then the fallbacks); null when none does.</summary>
        private async Task<CertifiedBlock?> TryFetchBlockAsync(ulong blockId, bool headerOnly)
        {
            foreach (var (name, api) in Algods())
            {
                try
                {
                    var block = await api.GetBlockAsync(blockId, Format.Msgpack, headerOnly);
                    if (block?.Block != null) return block;
                    _logger.LogWarning("{algod} returned no block {blockId}", name, blockId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{algod} failed to return block {blockId}", name, blockId);
                }
            }
            return null;
        }

        private const int LostBlockRecoveryAttempts = 20;
        private static readonly TimeSpan LostBlockRecoveryDelay = TimeSpan.FromSeconds(30);

        private async Task ProcessBlockWorkAsync(ulong blockId, CancellationToken cancellationToken)
        {
            _pending.Open(blockId, null);
            try
            {
                _logger.LogInformation("Loading block {blockId}", blockId);
                CertifiedBlock? block = null;
                for (var attempt = 1; attempt <= BlockFetchAttempts && block?.Block == null; attempt++)
                {
                    if (attempt > 1) await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                    block = await TryFetchBlockAsync(blockId, headerOnly: false);
                }
                if (block?.Block == null)
                {
                    // A few seconds of algod trouble must not turn into a permanently skipped block: the fetch keeps being
                    // retried in the background for a bounded time while the latest-block watermark (and StoredThrough)
                    // wait for the block, so even a restart in between re-processes it.
                    _logger.LogWarning("Block {blockId} not found after {attempts} attempts - retrying in the background for up to {minutes} minutes",
                        blockId, BlockFetchAttempts, LostBlockRecoveryAttempts * LostBlockRecoveryDelay.TotalMinutes);
                    _ = RecoverLostBlockAsync(blockId, cancellationToken);
                    return;
                }
                await ProcessFetchedBlockAsync(blockId, block, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // shutdown: the block is not closed (it must not complete) - the restart re-processes it from StoredThrough
                throw;
            }
        }

        private async Task RecoverLostBlockAsync(ulong blockId, CancellationToken cancellationToken)
        {
            try
            {
                for (var attempt = 1; attempt <= LostBlockRecoveryAttempts; attempt++)
                {
                    await Task.Delay(LostBlockRecoveryDelay, cancellationToken);
                    var block = await TryFetchBlockAsync(blockId, headerOnly: false);
                    if (block?.Block == null) continue;
                    _logger.LogInformation("Block {blockId} recovered on background attempt {attempt}", blockId, attempt);
                    await ProcessFetchedBlockAsync(blockId, block, cancellationToken);
                    return;
                }
                // Lost for the indexer itself as well (see CLAUDE.md, known limits): the watermark must not wait for ever.
                _logger.LogError("Block {blockId} could not be fetched for {minutes} minutes - its events are lost", blockId, LostBlockRecoveryAttempts * LostBlockRecoveryDelay.TotalMinutes);
                _pending.Drop(blockId);
                _blockTracker?.MarkCompleted(blockId, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // shutdown: the restart re-processes the block from StoredThrough
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recovering block {blockId} failed", blockId);
            }
        }

        private async Task ProcessFetchedBlockAsync(ulong blockId, CertifiedBlock block, CancellationToken cancellationToken)
        {
            try
            {
                _pending.Open(blockId, block.Block!.Timestamp != null ? Convert.ToInt64(block.Block.Timestamp) : null);
                _logger.LogInformation("Found transactions: {txCount}", block.Block.Transactions?.Count ?? 0);
                // A transaction whose processing threw (asset / pool lookup hiccup) would otherwise be missing from a block
                // that then counts as completely stored; re-processing is idempotent (registrations are keyed by tx id).
                var failed = await _transactionProcessor.ProcessBlock(block, this, this, cancellationToken);
                for (var attempt = 2; failed.Count > 0 && attempt <= BlockProcessAttempts; attempt++)
                {
                    _logger.LogWarning("{failed} transaction(s) of block {blockId} failed to process - processing them again (attempt {attempt})", failed.Count, blockId, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(attempt - 1), cancellationToken);
                    // position 0 means the block as a whole failed: everything is processed again
                    failed = await _transactionProcessor.ProcessBlock(block, this, this, cancellationToken, failed.Contains(0UL) ? null : failed.ToHashSet());
                }
                if (failed.Count > 0)
                {
                    _logger.LogError("{failed} transaction(s) of block {blockId} could not be processed after {attempts} attempts - their events are lost (see CLAUDE.md, known limits)", failed.Count, blockId, BlockProcessAttempts);
                }
                _pending.Close(blockId);

                await FlushPendingAsync(cancellationToken);

                await _blockRepository.PublishToHub(Model.Data.Block.FromAlgorandBlock(block.Block), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
                // Whatever was registered before the exception is flushed now (best effort) or with the next block, and only
                // then does the block count as stored for the latest-block watermark.
                _pending.Close(blockId);
                try
                {
                    await FlushPendingAsync(cancellationToken);
                }
                catch (Exception flushEx) when (flushEx is not OperationCanceledException)
                {
                    _logger.LogError(flushEx, "Flushing pending documents after the failure of block {blockId} failed too", blockId);
                }
            }
        }

        /// <summary>
        /// Stores every closed pending block (one bulk call per index) and completes exactly the blocks whose documents
        /// were acknowledged (the repositories answer true for "nothing to persist" - empty batch, no Elasticsearch).
        /// </summary>
        private async Task FlushPendingAsync(CancellationToken cancellationToken)
        {
            var flush = await _pending.FlushAsync(
                trades => _tradeRepository.StoreTradesAsync(trades, cancellationToken),
                liquidity => _liquidityRepository.StoreLiquidityUpdatesAsync(liquidity, cancellationToken),
                cancellationToken);
            if (flush.StoreFailed)
            {
                _logger.LogWarning("Storing pending documents failed; the closed block(s) stay pending and are retried with the next block");
            }
            foreach (var (round, timestamp) in flush.Abandoned)
            {
                // storage rejected the batch for the whole give-up window (see CLAUDE.md, known limits): lost, the watermark moves on
                _logger.LogError("Documents of block {round} could not be stored for {minutes} minutes - abandoned, its events are lost", round, PendingBlockBatches.DefaultGiveUpAfter.TotalMinutes);
                _blockTracker?.MarkCompleted(round, timestamp);
            }
            foreach (var (round, timestamp) in flush.Completed) _blockTracker?.MarkCompleted(round, timestamp);
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

            // Persist where the stored data really ends, so a graceful restart neither loses nor replays blocks. Closed batches
            // (a block whose task finished but whose flush failed or threw) get one more chance first.
            if (Indexer != null && _blockTracker?.CompletedThrough != null && _indexerRepository != null)
            {
                try
                {
                    await FlushPendingAsync(CancellationToken.None);
                    RecordStoredThrough();
                    await _indexerRepository.StoreIndexerAsync(Indexer, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not persist the stored-through round on shutdown");
                }
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