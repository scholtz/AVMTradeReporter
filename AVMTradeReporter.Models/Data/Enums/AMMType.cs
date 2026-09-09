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
        WeightedAMM,
        /// <summary>
        /// Tick based concentrated liquidity AMM (Uniswap v3 style, e.g. Pact CLAMM). Positions are ranges between ticks,
        /// the pool exposes only the current sqrt price / tick; reserves are tracked from trade and liquidity events.
        /// </summary>
        TickBasedCLAMM
    }
}
