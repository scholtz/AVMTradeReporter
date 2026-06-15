using AVMTradeReporter.Models.Data.Enums;

namespace AVMTradeReporter.Model.DTO
{
    public class TradeFilter
    {
        public ulong? AssetIdIn { get; set; }
        public ulong? AssetIdOut { get; set; }
        public ulong? AssetId { get; set; }
        public ulong? AssetIdA { get; set; }
        public ulong? AssetIdB { get; set; }
        public string? TxId { get; set; }
        public string? Trader { get; set; }
        public string? PoolAddress { get; set; }
        public ulong? PoolAppId { get; set; }
        public DEXProtocol? Protocol { get; set; }
        public TxState? TradeState { get; set; }
        public ulong? BlockFrom { get; set; }
        public ulong? BlockTo { get; set; }
        public DateTimeOffset? TimestampFrom { get; set; }
        public DateTimeOffset? TimestampTo { get; set; }
        public decimal? MinValueUSD { get; set; }
        public decimal? MaxValueUSD { get; set; }
        public decimal? MinFeesUSD { get; set; }
        public decimal? MaxFeesUSD { get; set; }
        public ulong? MinAmountIn { get; set; }
        public ulong? MaxAmountIn { get; set; }
        public ulong? MinAmountOut { get; set; }
        public ulong? MaxAmountOut { get; set; }
        public string? SortBy { get; set; }
        public string? SortDirection { get; set; }
        public int Offset { get; set; }
        public int Size { get; set; } = 100;

        public bool UsesAdvancedFilters =>
            AssetId.HasValue ||
            AssetIdA.HasValue ||
            AssetIdB.HasValue ||
            !string.IsNullOrWhiteSpace(Trader) ||
            !string.IsNullOrWhiteSpace(PoolAddress) ||
            PoolAppId.HasValue ||
            Protocol.HasValue ||
            TradeState.HasValue ||
            BlockFrom.HasValue ||
            BlockTo.HasValue ||
            TimestampFrom.HasValue ||
            TimestampTo.HasValue ||
            MinValueUSD.HasValue ||
            MaxValueUSD.HasValue ||
            MinFeesUSD.HasValue ||
            MaxFeesUSD.HasValue ||
            MinAmountIn.HasValue ||
            MaxAmountIn.HasValue ||
            MinAmountOut.HasValue ||
            MaxAmountOut.HasValue ||
            !string.IsNullOrWhiteSpace(SortBy) ||
            !string.IsNullOrWhiteSpace(SortDirection);
    }
}
