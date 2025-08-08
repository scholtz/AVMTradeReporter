using System.Text.Json.Serialization;

namespace AVMTradeReporter.Model.Data
{
    public class Pool
    {
        public string PoolAddress { get; set; }
        public ulong PoolAppId { get; set; }
        public ulong AssetIdA { get; set; }
        public ulong AssetIdB { get; set; }
        public ulong AssetIdLP { get; set; }
        public ulong AssetAmountA { get; set; }
        public ulong AssetAmountB { get; set; }
        public ulong AssetAmountLP { get; set; }
        public ulong A { get; set; }
        public ulong B { get; set; }
        public ulong L { get; set; }
        public DEXProtocol Protocol { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }
}
