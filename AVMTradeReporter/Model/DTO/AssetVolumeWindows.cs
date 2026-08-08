namespace AVMTradeReporter.Model.DTO
{
    /// <summary>
    /// Per-asset trading volumes in USD for the current and previous 1h/24h windows, computed
    /// directly from confirmed trades in Elasticsearch (unlike BiatecAsset.Volume1H/Volume24H,
    /// which are running counters that only re-window during pool refresh and go stale between
    /// refreshes). Each trade credits half of its ValueUSD to each of its two assets, matching
    /// the asset-volume convention used elsewhere (sum of pool volumes / 2).
    /// </summary>
    /// <param name="Volume1H">Volume in [now-1h, now).</param>
    /// <param name="Volume1HPrev">Volume in [now-2h, now-1h).</param>
    /// <param name="Volume24H">Volume in [now-24h, now).</param>
    /// <param name="Volume24HPrev">Volume in [now-48h, now-24h).</param>
    public record AssetVolumeWindows(decimal Volume1H, decimal Volume1HPrev, decimal Volume24H, decimal Volume24HPrev);
}
