using System.Text.Json.Serialization;

namespace AVMTradeReporter.Model.Data
{
    public class Pool
    {
        public string PoolAddress { get; set; } = string.Empty;
        public ulong PoolAppId { get; set; }
        public ulong? AssetIdA { get; set; }
        public ulong? AssetIdB { get; set; }
        public ulong? AssetIdLP { get; set; }
        public ulong? A { get; set; }
        public ulong? B { get; set; }
        public ulong? L { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DEXProtocol Protocol { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }
}
