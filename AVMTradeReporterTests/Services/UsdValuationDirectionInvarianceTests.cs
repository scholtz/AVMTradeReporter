using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AVMTradeReporterTests.Services;

public class UsdValuationDirectionInvarianceTests
{
    [Test]
    public async Task RegisterTrade_WhenDirectionReversed_UsdValueAndPriceRemainStable()
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

        var asset1 = await assetRepo.GetAssetAsync(1);
        asset1!.PriceUSD = 2m;
        await assetRepo.SetAssetAsync(asset1);

        var asset2 = await assetRepo.GetAssetAsync(2);
        asset2!.PriceUSD = 1m;
        await assetRepo.SetAssetAsync(asset2);

        await poolRepo.StorePoolAsync(new AVMTradeReporter.Models.Data.Pool { PoolAddress = "pool-rev", LPFee = 0.003m, ProtocolFeePortion = 0.2m }, true, CancellationToken.None);

        var tA = new Trade
        {
            TxId = "rev-a",
            PoolAddress = "pool-rev",
            AssetIdIn = 1,
            AssetIdOut = 2,
            AssetAmountIn = 1_000_000,
            AssetAmountOut = 2_000_000
        };

        var tB = new Trade
        {
            TxId = "rev-b",
            PoolAddress = "pool-rev",
            AssetIdIn = 2,
            AssetIdOut = 1,
            AssetAmountIn = 2_000_000,
            AssetAmountOut = 1_000_000
        };

        await ((ITradeService)service).RegisterTrade(tA, CancellationToken.None);
        await ((ITradeService)service).RegisterTrade(tB, CancellationToken.None);

        Assert.That(tA.ValueUSD, Is.EqualTo(tB.ValueUSD));
        Assert.That(tA.PriceUSD, Is.EqualTo(2m).Within(0.0000000001m));
        Assert.That(tB.PriceUSD, Is.EqualTo(2m).Within(0.0000000001m));
    }

    [Test]
    public async Task RegisterLiquidity_WhenSidesSwapped_UsdValueRemainsStable()
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

        var asset10 = await assetRepo.GetAssetAsync(10);
        asset10!.PriceUSD = 2m;
        await assetRepo.SetAssetAsync(asset10);

        var asset11 = await assetRepo.GetAssetAsync(11);
        asset11!.PriceUSD = 1m;
        await assetRepo.SetAssetAsync(asset11);

        var lA = new Liquidity
        {
            TxId = "l-swap-a",
            AssetIdA = 10,
            AssetIdB = 11,
            AssetAmountA = 2_000_000,
            AssetAmountB = 5_000_000
        };

        var lB = new Liquidity
        {
            TxId = "l-swap-b",
            AssetIdA = 11,
            AssetIdB = 10,
            AssetAmountA = 5_000_000,
            AssetAmountB = 2_000_000
        };

        await ((ILiquidityService)service).RegisterLiquidity(lA, CancellationToken.None);
        await ((ILiquidityService)service).RegisterLiquidity(lB, CancellationToken.None);

        Assert.That(lA.ValueUSD, Is.EqualTo(lB.ValueUSD));
        Assert.That(lA.ValueUSD, Is.EqualTo(4.5m).Within(0.0000000001m));
    }
}
