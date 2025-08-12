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
        public decimal? PMin { get; set; }
        public decimal? PMax { get; set; }
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

        public decimal VirualAmountA
        {
            get
            {
                if (AMMType == Data.AMMType.ConcentratedLiquidityAMM)
                {
                    // calculate virtual amount for concentrated liquidity AMM
                    if (PMin.HasValue && PMax.HasValue && A.HasValue && B.HasValue && L.HasValue)
                    {
                        var a = Convert.ToDecimal(A.Value) / 1000000000;
                        var b = Convert.ToDecimal(B.Value) / 1000000000;
                        var p = Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(PMin)));
                        var r = Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(PMax)));
                        var q = p / r - 1;
                        var eb = a * p + b / r;
                        var d = eb * eb - 4 * a * b * q;
                        var c = 2 * q;
                        var l = (-eb - Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(d)))) / c;
                        // var l = Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(x)));
                        //var l = Convert.ToDecimal(L.Value / 1000000000);
                        return a + l / r;
                    }
                    return 0;
                }
                else
                {
                    return A ?? 0 + AF ?? 0;
                }
            }
        }
        public decimal VirualAmountB
        {
            get
            {
                if (AMMType == Data.AMMType.ConcentratedLiquidityAMM)
                {
                    // calculate virtual amount for concentrated liquidity AMM
                    if (PMin.HasValue && PMax.HasValue && A.HasValue && B.HasValue && L.HasValue)
                    {
                        var a = Convert.ToDecimal(A.Value) / 1000000000;
                        var b = Convert.ToDecimal(B.Value) / 1000000000;
                        var p = Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(PMin)));
                        var r = Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(PMax)));
                        var q = p / r - 1;
                        var eb = a * p + b / r;
                        var d = eb * eb - 4 * a * b * q;
                        var c = 2 * q;
                        var l = (-eb - Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(d)))) / c;
                        //var l = Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(x)));

                        return b + l * p;
                        //return Convert.ToDecimal(B.Value) / 1000000000 + Convert.ToDecimal((Convert.ToDouble(L.Value / 1000000000)) * Math.Sqrt(Convert.ToDouble(PMin.Value)));
                    }
                    return 0;
                }
                else
                {
                    return B ?? 0 + BF ?? 0;
                }
            }
        }
    }
}
