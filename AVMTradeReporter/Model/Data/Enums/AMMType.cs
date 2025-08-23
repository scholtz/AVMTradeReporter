using System.Text.Json.Serialization;

namespace AVMTradeReporter.Model.Data.Enums
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AMMType
    {
        OldAMM,
        StableSwap,
        ConcentratedLiquidityAMM
    }
}
