using AVMTradeReporter.Models.Data.Enums;
using System.Text.Json.Serialization;

namespace AVMTradeReporter.Models.Data
{
    
    public class Liquidity
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public LiqudityDirection Direction { get; set; }
        public ulong AssetIdA { get; set; }
        public ulong AssetIdB { get; set; }
        public ulong AssetIdLP { get; set; }
        public ulong AssetAmountA { get; set; }
        public ulong AssetAmountB { get; set; }
        public ulong AssetAmountLP { get; set; }
        public ulong A { get; set; }
        public ulong B { get; set; }
        // protocol fees in A asset
        public ulong? AF { get; set; }
        // protocol fees in B asset
        public ulong? BF { get; set; }
        public ulong L { get; set; }
        public string TxId { get; set; } = string.Empty;
        public ulong BlockId { get; set; }
        public string TxGroup { get; set; } = string.Empty;
        public DateTimeOffset? Timestamp { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DEXProtocol Protocol { get; set; }
        public string LiquidityProvider { get; set; } = string.Empty;
        public string PoolAddress { get; set; } = string.Empty;
        public ulong PoolAppId { get; set; }
        public string TopTxId { get; set; } = string.Empty;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public TxState TxState { get; set; } = TxState.TxPool;
    }
}
