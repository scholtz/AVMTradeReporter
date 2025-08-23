using AVMTradeReporter.Model.Data.Enums;

namespace AVMTradeReporter.Model.Data
{
    public class BiatecAsset : Algorand.Algod.Model.Asset
    {
        public AssetType Type { get; set; } = AssetType.ASA;
        public decimal PriceUSD { get; set; }
        public decimal TVL_USD { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }
}
