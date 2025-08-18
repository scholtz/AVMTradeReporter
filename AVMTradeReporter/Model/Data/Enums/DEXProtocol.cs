using System.Text.Json.Serialization;

namespace AVMTradeReporter.Model.Data.Enums
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DEXProtocol
    {
        Pact,
        Tiny,
        Biatec
    }
}
