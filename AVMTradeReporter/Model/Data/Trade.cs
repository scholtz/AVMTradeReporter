using Algorand;
using System.Text.Json.Serialization;

namespace AVMTradeReporter.Model.Data
{
    public enum DEXProtocol
    {
        Pact,
        Tiny,
        Biatec
    }
    public enum TradeState
    {
        TxPool,
        Confirmed
    }
    public class Trade
    {
        public ulong AssetIdIn { get; set; }
        public ulong AssetIdOut { get; set; }
        public ulong AssetAmountIn { get; set; }
        public ulong AssetAmountOut { get; set; }
        public string TxId { get; set; } = string.Empty;
        public ulong BlockId { get; set; }
        public string TxGroup { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DEXProtocol Protocol { get; set; }
        public string Trader { get; set; }
        public string PoolAddress { get; set; }
        public ulong PoolAppId { get; set; }
        public string TopTxId { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public TradeState TradeState { get; set; } = TradeState.TxPool;

        public ulong A { get; set; }
        public ulong B { get; set; }
        public ulong L { get; set; }
    }
}
