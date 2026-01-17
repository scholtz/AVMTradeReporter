using AVMIndexReporter.Repository;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AVMTradeReporterTests.Services;

public partial class TradeReporterBackgroundServiceUsdValuationTests
{
    [Test]
    public async Task RegisterTrade_WhenBothSidesPriced_SetsValueUsdAsAverage()
    {
        var assetRepo = new MockAssetRepository();
        var poolRepo = new MockPoolRepository();

        var logger = new LoggerFactory().CreateLogger<TradeReporterBackgroundService>();
        var config = Options.Create(new AppConfiguration
        {
            BlockProcessing = new BlockProcessingConfiguration { MaxConcurrentTasks = 1 }
        });

        var service = new TradeReporterBackgroundService(
            logger,
            config,
            indexerRepository: null!,
            tradeRepository: null!,
            liquidityRepository: null!,
            poolRepository: poolRepo,
            assetRepository: assetRepo,
            transactionProcessor: null!,
            blockRepository: null!);

        var assetIn = await assetRepo.GetAssetAsync(1);
        assetIn!.PriceUSD = 2m;
        await assetRepo.SetAssetAsync(assetIn);

        var assetOut = await assetRepo.GetAssetAsync(2);
        assetOut!.PriceUSD = 1m;
        await assetRepo.SetAssetAsync(assetOut);

        var trade = new Trade
        {
            TxId = "t1",
            PoolAddress = "pool-1",
            AssetIdIn = 1,
            AssetIdOut = 2,
            AssetAmountIn = 1_000_000,  // 1
            AssetAmountOut = 2_000_000  // 2
        };

        await poolRepo.StorePoolAsync(new AVMTradeReporter.Models.Data.Pool { PoolAddress = "pool-1", LPFee = 0.003m, ProtocolFeePortion = 0.2m }, true, CancellationToken.None);

        await ((ITradeService)service).RegisterTrade(trade, CancellationToken.None);

        Assert.That(trade.ValueUSD, Is.EqualTo(2m));
    }

    [Test]
    public async Task RegisterTrade_WhenPoolFeeAndInputPriced_ComputesFeeUsdAndSplits()
    {
        // Arrange
        var assetRepo = new MockAssetRepository();
        var poolRepo = new MockPoolRepository();

        var logger = new LoggerFactory().CreateLogger<TradeReporterBackgroundService>();
        var config = Options.Create(new AppConfiguration
        {
            BlockProcessing = new BlockProcessingConfiguration { MaxConcurrentTasks = 1 }
        });

        var service = new TradeReporterBackgroundService(
            logger,
            config,
            indexerRepository: null!,
            tradeRepository: null!,
            liquidityRepository: null!,
            poolRepository: poolRepo,
            assetRepository: assetRepo,
            transactionProcessor: null!,
            blockRepository: null!);

        var assetIn = await assetRepo.GetAssetAsync(1);
        assetIn!.PriceUSD = 2m; // 2 USD per 1.0 input asset
        await assetRepo.SetAssetAsync(assetIn);

        await poolRepo.StorePoolAsync(new AVMTradeReporter.Models.Data.Pool
        {
            PoolAddress = "pool-fee",
            LPFee = 0.003m,
            ProtocolFeePortion = 0.2m
        }, true, CancellationToken.None);

        var trade = new Trade
        {
            TxId = "t-fee",
            PoolAddress = "pool-fee",
            AssetIdIn = 1,
            AssetIdOut = 2,
            AssetAmountIn = 1_000_000, // 1.0
            AssetAmountOut = 2_000_000
        };

        // Act
        await ((ITradeService)service).RegisterTrade(trade, CancellationToken.None);

        // Assert
        // input USD = 1.0 * 2 = 2
        // gross fee = 2 * 0.003 = 0.006
        // protocol fee = 0.006 * 0.2 = 0.0012
        // provider fee = 0.0048
        Assert.That(trade.FeesUSD, Is.EqualTo(0.006m).Within(0.0000000001m));
        Assert.That(trade.FeesUSDProtocol, Is.EqualTo(0.0012m).Within(0.0000000001m));
        Assert.That(trade.FeesUSDProvider, Is.EqualTo(0.0048m).Within(0.0000000001m));
    }

    [Test]
    public async Task RegisterTrade_WhenOnlyInputSidePriced_SetsValueUsdFromInput()
    {
        var assetRepo = new MockAssetRepository();
        var poolRepo = new MockPoolRepository();

        var logger = new LoggerFactory().CreateLogger<TradeReporterBackgroundService>();
        var config = Options.Create(new AppConfiguration
        {
            BlockProcessing = new BlockProcessingConfiguration { MaxConcurrentTasks = 1 }
        });

        var service = new TradeReporterBackgroundService(
            logger,
            config,
            indexerRepository: null!,
            tradeRepository: null!,
            liquidityRepository: null!,
            poolRepository: poolRepo,
            assetRepository: assetRepo,
            transactionProcessor: null!,
            blockRepository: null!);

        var assetIn = await assetRepo.GetAssetAsync(1);
        assetIn!.PriceUSD = 2m;
        await assetRepo.SetAssetAsync(assetIn);

        var trade = new Trade
        {
            TxId = "t2",
            PoolAddress = "pool-2",
            AssetIdIn = 1,
            AssetIdOut = 2,
            AssetAmountIn = 1_500_000,  // 1.5
            AssetAmountOut = 3_000_000  // 3
        };

        await poolRepo.StorePoolAsync(new AVMTradeReporter.Models.Data.Pool { PoolAddress = "pool-2", LPFee = 0.003m, ProtocolFeePortion = 0.2m }, true, CancellationToken.None);

        await ((ITradeService)service).RegisterTrade(trade, CancellationToken.None);

        Assert.That(trade.ValueUSD, Is.EqualTo(3m));
    }

    [Test]
    public async Task RegisterTrade_WhenValueUsdComputed_SetsPerSideUsdPrices()
    {
        var assetRepo = new MockAssetRepository();
        var poolRepo = new MockPoolRepository();

        var logger = new LoggerFactory().CreateLogger<TradeReporterBackgroundService>();
        var config = Options.Create(new AppConfiguration
        {
            BlockProcessing = new BlockProcessingConfiguration { MaxConcurrentTasks = 1 }
        });

        var service = new TradeReporterBackgroundService(
            logger,
            config,
            indexerRepository: null!,
            tradeRepository: null!,
            liquidityRepository: null!,
            poolRepository: poolRepo,
            assetRepository: assetRepo,
            transactionProcessor: null!,
            blockRepository: null!);

        var assetIn = await assetRepo.GetAssetAsync(1);
        assetIn!.PriceUSD = 2m;
        await assetRepo.SetAssetAsync(assetIn);

        var outAsset = await assetRepo.GetAssetAsync(2);
        outAsset!.PriceUSD = 1m;
        await assetRepo.SetAssetAsync(outAsset);

        var trade = new Trade
        {
            TxId = "t3",
            PoolAddress = "pool-3",
            AssetIdIn = 1,
            AssetIdOut = 2,
            AssetAmountIn = 1_000_000,
            AssetAmountOut = 2_000_000
        };

        await poolRepo.StorePoolAsync(new AVMTradeReporter.Models.Data.Pool { PoolAddress = "pool-3", LPFee = 0.003m, ProtocolFeePortion = 0.2m }, true, CancellationToken.None);

        await ((ITradeService)service).RegisterTrade(trade, CancellationToken.None);

        // ValueUSD = avg(2, 2) = 2
        // Price per In asset = 2 / 1 = 2
        // Price per Out asset = 2 / 2 = 1
        Assert.That(trade.ValueUSD, Is.EqualTo(2m));
        Assert.That(trade.PriceAssetInUSD, Is.EqualTo(2m));
        Assert.That(trade.PriceAssetOutUSD, Is.EqualTo(1m));
    }

    [Test]
    public async Task RegisterLiquidity_WhenOnlyOneSidePriced_SetsValueUsd()
    {
        var assetRepo = new MockAssetRepository();
        var poolRepo = new MockPoolRepository();

        var logger = new LoggerFactory().CreateLogger<TradeReporterBackgroundService>();
        var config = Options.Create(new AppConfiguration
        {
            BlockProcessing = new BlockProcessingConfiguration { MaxConcurrentTasks = 1 }
        });

        var service = new TradeReporterBackgroundService(
            logger,
            config,
            indexerRepository: null!,
            tradeRepository: null!,
            liquidityRepository: null!,
            poolRepository: poolRepo,
            assetRepository: assetRepo,
            transactionProcessor: null!,
            blockRepository: null!);

        var assetA = await assetRepo.GetAssetAsync(10);
        assetA!.PriceUSD = 2m;
        await assetRepo.SetAssetAsync(assetA);

        var liq = new Liquidity
        {
            TxId = "l1",
            AssetIdA = 10,
            AssetIdB = 11,
            AssetAmountA = 2_000_000, // 2
            AssetAmountB = 5_000_000  // 5
        };

        await ((ILiquidityService)service).RegisterLiquidity(liq, CancellationToken.None);

        Assert.That(liq.ValueUSD, Is.EqualTo(4m));
    }
}
