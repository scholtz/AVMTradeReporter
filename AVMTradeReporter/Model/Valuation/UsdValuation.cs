using AVMTradeReporter.Model.Data;

namespace AVMTradeReporter.Model.Valuation;

/// <summary>
/// Helpers for computing USD valuations from on-chain amounts.
/// </summary>
public static class UsdValuation
{
    /// <summary>
    /// Converts a base-unit amount (micro/unscaled integer on-chain representation) to a decimal amount
    /// using the asset decimals.
    /// </summary>
    /// <param name="baseUnits">The on-chain amount in base units.</param>
    /// <param name="decimals">The number of decimals for the asset.</param>
    /// <returns>The scaled decimal amount.</returns>
    public static decimal ToDecimalAmount(ulong baseUnits, ulong decimals)
    {
        if (baseUnits == 0) return 0m;
        if (decimals == 0) return baseUnits;

        // Using decimal to avoid floating point rounding issues.
        if (decimals > 28) return 0m;
        var divisor = (decimal)Math.Pow(10, (double)decimals);
        return baseUnits / divisor;
    }

    /// <summary>
    /// Computes USD value for an on-chain base-unit amount.
    /// </summary>
    /// <param name="baseUnits">The on-chain amount in base units.</param>
    /// <param name="asset">The asset containing decimals and USD price.</param>
    /// <returns>The USD value, or <c>null</c> if the asset does not have a positive USD price configured.</returns>
    public static decimal? TryComputeUsdValue(ulong baseUnits, BiatecAsset? asset)
    {
        if (baseUnits == 0) return 0m;
        if (asset?.Params?.Decimals == null) return null;
        if (asset.PriceUSD <= 0) return null;

        var amount = ToDecimalAmount(baseUnits, asset.Params.Decimals);
        return amount * asset.PriceUSD;
    }

    /// <summary>
    /// Computes USD fee from an on-chain fee amount.
    /// </summary>
    /// <param name="feeBaseUnits">The on-chain fee amount in base units.</param>
    /// <param name="feeAsset">The asset in which the fee is denominated.</param>
    /// <returns>The USD fee, or <c>null</c> if <paramref name="feeBaseUnits"/> is <c>null</c> or price is unavailable.</returns>
    public static decimal? TryComputeUsdFee(ulong? feeBaseUnits, BiatecAsset? feeAsset)
    {
        if (feeBaseUnits == null) return null;
        return TryComputeUsdValue(feeBaseUnits.Value, feeAsset);
    }

    /// <summary>
    /// Computes a USD price for the trade as USD per one unit of the out asset.
    /// </summary>
    /// <param name="tradeUsdValue">USD valuation of the trade.</param>
    /// <param name="assetAmountOutBaseUnits">The out amount in base units.</param>
    /// <param name="assetOut">The out asset (used only for decimals).</param>
    /// <returns>USD per out-asset unit, or <c>null</c> if inputs are invalid.</returns>
    public static decimal? TryComputeUsdTradePrice(decimal? tradeUsdValue, ulong assetAmountOutBaseUnits, BiatecAsset? assetOut)
    {
        if (tradeUsdValue == null) return null;
        if (assetAmountOutBaseUnits == 0) return null;
        if (assetOut?.Params?.Decimals == null) return null;

        var outAmount = ToDecimalAmount(assetAmountOutBaseUnits, assetOut.Params.Decimals);
        if (outAmount == 0) return null;
        return tradeUsdValue.Value / outAmount;
    }
}
