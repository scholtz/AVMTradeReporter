using AVMIndexReporter.Repository;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AVMTradeReporterTests.Services;

public class TradeReporterBackgroundServiceUsdValuationTests
{
    [Test]
    public async Task RegisterTrade_WhenBothSidesPriced_SetsValueUsdAsAverage()
    {
        var assetRepo = new MockAssetRepository();

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
            poolRepository: null!,
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
            AssetIdIn = 1,
            AssetIdOut = 2,
            AssetAmountIn = 1_000_000,  // 1
            AssetAmountOut = 2_000_000  // 2
        };

        await ((ITradeService)service).RegisterTrade(trade, CancellationToken.None);

        Assert.That(trade.ValueUSD, Is.EqualTo(2m));
    }

    [Test]
    public async Task RegisterTrade_WhenOnlyInputSidePriced_SetsValueUsdFromInput()
    {
        var assetRepo = new MockAssetRepository();

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
            poolRepository: null!,
            assetRepository: assetRepo,
            transactionProcessor: null!,
            blockRepository: null!);

        var assetIn = await assetRepo.GetAssetAsync(1);
        assetIn!.PriceUSD = 2m;
        await assetRepo.SetAssetAsync(assetIn);

        var trade = new Trade
        {
            TxId = "t2",
            AssetIdIn = 1,
            AssetIdOut = 2,
            AssetAmountIn = 1_500_000,  // 1.5
            AssetAmountOut = 3_000_000  // 3
        };

        await ((ITradeService)service).RegisterTrade(trade, CancellationToken.None);

        Assert.That(trade.ValueUSD, Is.EqualTo(3m));
    }

    [Test]
    public async Task RegisterTrade_WhenValueUsdComputed_SetsPriceUsdPerOutAsset()
    {
        var assetRepo = new MockAssetRepository();

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
            poolRepository: null!,
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
            AssetIdIn = 1,
            AssetIdOut = 2,
            AssetAmountIn = 1_000_000,
            AssetAmountOut = 2_000_000
        };

        await ((ITradeService)service).RegisterTrade(trade, CancellationToken.None);

        Assert.That(trade.PriceUSD, Is.EqualTo(1m));
    }

    [Test]
    public async Task RegisterLiquidity_WhenOnlyOneSidePriced_SetsValueUsd()
    {
        var assetRepo = new MockAssetRepository();

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
            poolRepository: null!,
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
