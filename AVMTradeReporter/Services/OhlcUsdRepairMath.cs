using AVMTradeReporter.Models.Data;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Pure rebuild math for the one-shot USD OHLC repair (<see cref="OhlcUsdRepairService"/>).
    ///
    /// Historical `-usd-` candles were derived from Trade.ValueUSD (the average of both legs'
    /// cached USD values), which let mispriced illiquid counter-tokens write absurd highs/lows
    /// into major assets' USD series. The `-asset-` candles, in contrast, are exact on-chain
    /// exchange rates and were never polluted — so a correct USD candle for asset X can be
    /// rebuilt from the X↔anchor `-asset-` candle plus the anchor's USD price, mirroring the
    /// trusted-anchor pricing that OHLCRepository now applies at ingestion time.
    /// </summary>
    internal static class OhlcUsdRepairMath
    {
        /// <summary>
        /// Rebuilds the USD candle for <paramref name="assetId"/> from an `-asset-` candle of a
        /// pair that contains it, anchored at <paramref name="anchorUsdPrice"/> — the USD price of
        /// the pair's OTHER asset ($1 when the counter is the USD reference asset, ALGO's USD
        /// price for (0, X) pairs). Stored price is quote-per-base, so when the asset is the
        /// stored quote the rate is inverted, swapping the roles of high and low.
        /// Returns null when the candle cannot anchor a sane price (missing/non-positive OHLC
        /// components, non-positive anchor, or the asset not being part of the pair).
        /// </summary>
        public static OHLC? RebuildUsdCandle(ulong assetId, ulong usdReferenceAssetId, OHLC assetCandle, decimal anchorUsdPrice, DateTimeOffset lastUpdated)
        {
            if (anchorUsdPrice <= 0) return null;
            if (assetCandle.Open is not > 0 || assetCandle.High is not > 0 || assetCandle.Low is not > 0 || assetCandle.Close is not > 0) return null;

            decimal open, high, low, close, volumeBase;
            if (assetCandle.AssetIdA == assetId)
            {
                open = assetCandle.Open.Value * anchorUsdPrice;
                high = assetCandle.High.Value * anchorUsdPrice;
                low = assetCandle.Low.Value * anchorUsdPrice;
                close = assetCandle.Close.Value * anchorUsdPrice;
                volumeBase = assetCandle.VolumeBase ?? 0m;
            }
            else if (assetCandle.AssetIdB == assetId)
            {
                open = anchorUsdPrice / assetCandle.Open.Value;
                high = anchorUsdPrice / assetCandle.Low.Value;
                low = anchorUsdPrice / assetCandle.High.Value;
                close = anchorUsdPrice / assetCandle.Close.Value;
                volumeBase = assetCandle.VolumeQuote ?? 0m;
            }
            else
            {
                return null;
            }

            return new OHLC
            {
                AssetIdA = assetId,
                AssetIdB = usdReferenceAssetId,
                Interval = assetCandle.Interval,
                InUSDValuation = true,
                StartTime = assetCandle.StartTime,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                VolumeBase = volumeBase,
                // Best available approximation for a rebuilt bucket: total base volume at the
                // bucket's closing USD price (ingestion accumulates per-trade USD amounts, which
                // cannot be reconstructed from the aggregate).
                VolumeQuote = volumeBase * close,
                Trades = assetCandle.Trades,
                LastUpdated = lastUpdated
            };
        }
    }
}
