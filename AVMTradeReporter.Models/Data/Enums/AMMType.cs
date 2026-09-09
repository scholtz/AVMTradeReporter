using System.Text.Json.Serialization;

namespace AVMTradeReporter.Models.Data.Enums
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AMMType
    {
        OldAMM,
        StableSwap,
        ConcentratedLiquidityAMM,
        /// <summary>
        /// Weighted constant-product AMM (Balancer style), e.g. Pact weighted pools. Weights are stored in Pool.WeightA / Pool.WeightB.
        /// </summary>
        WeightedAMM
    }
}
