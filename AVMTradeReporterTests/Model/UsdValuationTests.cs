using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.Valuation;
using Algorand.Algod.Model;

namespace AVMTradeReporterTests.Model;

public class UsdValuationTests
{
    [Test]
    public void ToDecimalAmount_WhenDecimalsZero_ReturnsBaseUnits()
    {
        var result = UsdValuation.ToDecimalAmount(123UL, 0);
        Assert.That(result, Is.EqualTo(123m));
    }

    [Test]
    public void ToDecimalAmount_WhenDecimalsSix_ScalesCorrectly()
    {
        var result = UsdValuation.ToDecimalAmount(1_500_000UL, 6);
        Assert.That(result, Is.EqualTo(1.5m));
    }

    [Test]
    public void TryComputeUsdValue_WhenPriceUnavailable_ReturnsNull()
    {
        var asset = new BiatecAsset
        {
            Index = 1,
            Params = new AssetParams { Decimals = 6 },
            PriceUSD = 0m
        };

        var result = UsdValuation.TryComputeUsdValue(1_000_000UL, asset);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void TryComputeUsdValue_WhenPriceAvailable_ComputesUsd()
    {
        var asset = new BiatecAsset
        {
            Index = 1,
            Params = new AssetParams { Decimals = 6 },
            PriceUSD = 2m
        };

        var result = UsdValuation.TryComputeUsdValue(1_500_000UL, asset);
        Assert.That(result, Is.EqualTo(3m));
    }

    [Test]
    public void TryComputeUsdTradePrice_WhenInputsValid_ComputesUsdPerOutAsset()
    {
        var outAsset = new BiatecAsset
        {
            Index = 2,
            Params = new AssetParams { Decimals = 6 }
        };

        var result = UsdValuation.TryComputeUsdTradePrice(2m, 1_000_000UL, outAsset);
        Assert.That(result, Is.EqualTo(2m));
    }
}
