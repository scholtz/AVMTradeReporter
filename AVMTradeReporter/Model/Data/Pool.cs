using System.Text.Json.Serialization;

namespace AVMTradeReporter.Model.Data
{
    public enum AMMType
    {
        OldAMM,
        ConcentratedLiquidityAMM
    }
    public class Pool
    {
        public string PoolAddress { get; set; } = string.Empty;
        public ulong PoolAppId { get; set; }
        public ulong? AssetIdA { get; set; }
        public ulong? AssetIdB { get; set; }
        public ulong? AssetIdLP { get; set; }
        public ulong? A { get; set; }
        public ulong? B { get; set; }
        // protocol fees in A asset
        public ulong? AF { get; set; }
        // protocol fees in B asset
        public ulong? BF { get; set; }
        public ulong? L { get; set; }
        /// <summary>
        /// Verification class at Biatec Identity service.. Some pools allows swapping only between verified persons
        /// </summary>
        public ulong? VerificationClass { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DEXProtocol Protocol { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AMMType? AMMType { get; set; }
        /// <summary>
        /// DEX smart contract hash of the deployed application
        /// </summary>
        public string? ApprovalProgramHash { get; set; }
        /// <summary>
        /// Fee for providing the liquidity
        /// </summary>
        public decimal? LPFee { get; set; }
        /// <summary>
        /// If LP fee is 0,3%, and protocol fee is 50%, 0,15% is for the liquidity providers, and 0,15% fee is taken by the protocol owner
        /// </summary>
        public decimal? ProtocolFeePortion { get; set; }
    }
}
