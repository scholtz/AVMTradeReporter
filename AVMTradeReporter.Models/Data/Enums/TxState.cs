using System.Text.Json.Serialization;

namespace AVMTradeReporter.Models.Data.Enums
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TxState
    {
        TxPool,
        Confirmed
    }
}
