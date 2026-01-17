using AVMIndexReporter.Repository;
using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AVMTradeReporterTests.Services;

public partial class TradeReporterBackgroundServiceUsdValuationTests
{
    [Test]
    public async Task RegisterTrade_WhenValueUsdComputed_SetsPriceUsdPerCanonicalBaseAsset()
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
            TxId = "t3-override",
            PoolAddress = "pool-3",
            AssetIdIn = 1,
            AssetIdOut = 2,
            AssetAmountIn = 1_000_000,
            AssetAmountOut = 2_000_000
        };

        await poolRepo.StorePoolAsync(new AVMTradeReporter.Models.Data.Pool { PoolAddress = "pool-3", LPFee = 0.003m, ProtocolFeePortion = 0.2m }, true, CancellationToken.None);

        await ((ITradeService)service).RegisterTrade(trade, CancellationToken.None);

        Assert.That(trade.PriceUSD, Is.EqualTo(2m));
    }
}
