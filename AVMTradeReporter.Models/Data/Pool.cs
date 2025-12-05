using AVMTradeReporter.Models.Data.Enums;
using System.Text.Json.Serialization;
using System.Numerics;

namespace AVMTradeReporter.Models.Data
{

    public class Pool
    {
        public string PoolAddress { get; set; } = string.Empty;
        public ulong PoolAppId { get; set; }
        public ulong? AssetIdA { get; set; }
        public ulong? AssetADecimals { get; set; }
        public ulong? AssetIdB { get; set; }
        public ulong? AssetBDecimals { get; set; }
        public ulong? AssetIdLP { get; set; }
        public ulong? A { get; set; }
        public ulong? B { get; set; }
        public ulong? StableA { get; set; }
        public ulong? StableB { get; set; }
        public ulong? Amplifier { get; set; }
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
        /// <summary>
        /// Total USD value for asset A side of this pool (RealAmountA * AssetA USD price). To be populated by services after asset prices are updated.
        /// </summary>
        public decimal? TotalTVLAssetAInUSD { get; set; }
        /// <summary>
        /// Total USD value for asset B side of this pool (RealAmountB * AssetB USD price). To be populated by services after asset prices are updated.
        /// </summary>
        public decimal? TotalTVLAssetBInUSD { get; set; }

        public decimal VirtualAmountA
        {
            get
            {
                try
                {
                    if (AMMType == Enums.AMMType.ConcentratedLiquidityAMM)
                    {
                        // calculate virtual amount for concentrated liquidity AMM
                        if (PMin.HasValue && PMax.HasValue && A.HasValue && B.HasValue)
                        {
                            if (PMin == PMax)
                            {
                                // special case when PMin == PMax, we can calculate the virtual amount directly

                                return RealAmountA;
                            }

                            var a = Convert.ToDecimal(A.Value) / 1000000000;
                            var b = Convert.ToDecimal(B.Value) / 1000000000;
                            var p = Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(PMin)));
                            var r = Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(PMax)));

                            var q = p / r - 1;
                            var eb = a * p + b / r;
                            var d = eb * eb - 4 * a * b * q;
                            var c = 2 * q;
                            var l1 = (-eb - Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(d)))) / c;
                            var l2 = (-eb + Convert.ToDecimal(Math.Sqrt(Convert.ToDouble(d)))) / c;
                            var l = Math.Max(l1, l2);
                            return a + l / r;
                        }
                        return 0;
                    }
                    else if (AMMType == Enums.AMMType.StableSwap)
                    {
                        if (StableA.HasValue && StableB.HasValue && Amplifier.HasValue)
                        {
                            try
                            {
                                var amp = new BigInteger(Amplifier.Value);
                                var realA = new BigInteger(StableA.Value);
                                var realB = new BigInteger(StableB.Value);

                                var D = GetD(realA, realB, amp);

                                var delta = realA / 10000;
                                if (delta == 0) delta = 1;

                                var newB = GetY(realA + delta, amp, D);
                                var price = (decimal)(realB - newB) / (decimal)delta;

                                var dDecimal = (decimal)D / (decimal)Math.Pow(10, (double)(AssetADecimals ?? 0));
                                var virtualLiquidity = dDecimal / 2;

                                var priceReal = price * (decimal)Math.Pow(10, (double)(AssetADecimals ?? 0) - (double)(AssetBDecimals ?? 0));

                                if (priceReal > 0)
                                {
                                    return virtualLiquidity / (decimal)Math.Sqrt((double)priceReal);
                                }
                            }
                            catch
                            {
                                // fallback
                            }
                        }
                        // for stable swap, we can return the minimum of the two amounts as the virtual amount.. so that the pool is balanced for price 1:1
                        return Math.Min(RealAmountA, RealAmountB);
                    }
                    else
                    {
                        return RealAmountA;
                    }
                }
                catch (Exception ex)
                {
                    // Log the exception or handle it as needed
                    Console.Error.WriteLine($"Error calculating VirtualAmountA: {ex.Message}");
                    return 0;
                }
            }
        }
        public decimal RealAmountA
        {
            get
            {
                if (this.Protocol == DEXProtocol.Biatec)
                {
                    return Convert.ToDecimal(A) / 1000000000;
                }
                if (this.AMMType == Enums.AMMType.StableSwap)
                {
                    return (Convert.ToDecimal(StableA ?? 0) / Convert.ToDecimal(Math.Pow(10, Convert.ToDouble(AssetADecimals ?? 0))));
                }
                return Convert.ToDecimal(A) / Convert.ToDecimal(Math.Pow(10, Convert.ToDouble(AssetADecimals ?? 0))) + Convert.ToDecimal(AF) / Convert.ToDecimal(Math.Pow(10, Convert.ToDouble(AssetADecimals ?? 0)));
            }
        }
        public decimal VirtualAmountB
        {
            get
            {
                try
                {
                    if (AMMType == Enums.AMMType.ConcentratedLiquidityAMM)
                    {
                        // calculate virtual amount for concentrated liquidity AMM
                        if (PMin.HasValue && PMax.HasValue && A.HasValue && B.HasValue)
                        {
                            if (PMin == PMax)
                            {
                                // special case when PMin == PMax, we can calculate the virtual amount directly

                                return RealAmountA;
                            }
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
                    else if (AMMType == Enums.AMMType.StableSwap)
                    {
                        if (StableA.HasValue && StableB.HasValue && Amplifier.HasValue)
                        {
                            try
                            {
                                var amp = new BigInteger(Amplifier.Value);
                                var realA = new BigInteger(StableA.Value);
                                var realB = new BigInteger(StableB.Value);

                                var D = GetD(realA, realB, amp);

                                var delta = realA / 10000;
                                if (delta == 0) delta = 1;

                                var newB = GetY(realA + delta, amp, D);
                                var price = (decimal)(realB - newB) / (decimal)delta;

                                var dDecimal = (decimal)D / (decimal)Math.Pow(10, (double)(AssetADecimals ?? 0));
                                var virtualLiquidity = dDecimal / 2;

                                var priceReal = price * (decimal)Math.Pow(10, (double)(AssetADecimals ?? 0) - (double)(AssetBDecimals ?? 0));

                                if (priceReal > 0)
                                {
                                    return virtualLiquidity * (decimal)Math.Sqrt((double)priceReal);
                                }
                            }
                            catch
                            {
                                // fallback
                            }
                        }
                        // for stable swap, we can return the minimum of the two amounts as the virtual amount.. so that the pool is balanced for price 1:1
                        return Math.Min(RealAmountA, RealAmountB);
                    }
                    else
                    {
                        return RealAmountB;
                    }

                }
                catch (Exception ex)
                {
                    // Log the exception or handle it as needed
                    Console.Error.WriteLine($"Error calculating VirtualAmountB: {ex.Message}");
                    return 0;
                }
            }
        }
        public decimal RealAmountB
        {
            get
            {
                if (this.Protocol == DEXProtocol.Biatec)
                {
                    return Convert.ToDecimal(B) / 1000000000;
                }
                if (this.AMMType == Enums.AMMType.StableSwap)
                {
                    return (Convert.ToDecimal(StableB ?? 0) / Convert.ToDecimal(Math.Pow(10, Convert.ToDouble(AssetBDecimals ?? 0))));
                }
                return Convert.ToDecimal(B) / Convert.ToDecimal(Math.Pow(10, Convert.ToDouble(AssetBDecimals ?? 0))) + Convert.ToDecimal(BF) / Convert.ToDecimal(Math.Pow(10, Convert.ToDouble(AssetBDecimals ?? 0)));
            }
        }
        public Pool Reverse()
        {
            return new Pool
            {
                PoolAddress = PoolAddress,
                PoolAppId = PoolAppId,
                AssetIdA = AssetIdB,
                AssetADecimals = AssetBDecimals,
                AssetIdB = AssetIdA,
                AssetBDecimals = AssetADecimals,
                AssetIdLP = AssetIdLP,
                A = B,
                B = A,
                StableA = StableB,
                StableB = StableA,
                Amplifier = Amplifier,
                AF = BF,
                BF = AF,
                L = L,
                PMin = PMax == null ? null : 1 / PMax,
                PMax = PMin == null ? null : 1 / PMin,
                VerificationClass = VerificationClass,
                Protocol = Protocol,
                Timestamp = Timestamp,
                AMMType = AMMType,
                ApprovalProgramHash = ApprovalProgramHash,
                LPFee = LPFee,
                ProtocolFeePortion = ProtocolFeePortion,
                TotalTVLAssetAInUSD = TotalTVLAssetBInUSD, // swapped
                TotalTVLAssetBInUSD = TotalTVLAssetAInUSD
            };
        }

        private BigInteger GetD(BigInteger totalPrimary, BigInteger totalSecondary, BigInteger amp)
        {
            var nCoins = 2;
            var aPrecision = 1000;
            var nn = 4; // nCoins ^ nCoins

            var S = totalPrimary + totalSecondary;
            if (S == 0) return 0;

            var D = S;
            var Ann = amp * nn;

            var i = 0;
            var nPlusOne = nCoins + 1;

            for (i = 0; i < 64; i++)
            {
                var D_P = D;
                // D_P = D^3 / (4 * x * y)
                D_P = (D * D * D) / (totalPrimary * totalSecondary * nn);

                var DPrev = D;

                // numerator = D * ( (Ann * S / aPrecision) + D_P * nCoins )
                // divisor = ( (Ann - aPrecision) * D / aPrecision ) + (nPlusOne * D_P)
                
                var numerator = D * ((Ann * S / aPrecision) + D_P * nCoins);
                var divisor = ((Ann - aPrecision) * D / aPrecision) + (nPlusOne * D_P);

                if (divisor == 0) return D;

                D = numerator / divisor;

                if (D > DPrev)
                {
                    if (D - DPrev <= 1) break;
                }
                else
                {
                    if (DPrev - D <= 1) break;
                }
            }
            return D;
        }

        private BigInteger GetY(BigInteger otherTotal, BigInteger amp, BigInteger D)
        {
            var nCoins = 2;
            var aPrecision = 1000;
            var nn = 4;
            var Ann = amp * nn;

            var S = otherTotal;
            var P = otherTotal;

            var b = S + (D * aPrecision) / Ann;
            var c = (D * D * D * aPrecision) / (4 * P * Ann);

            BigInteger diff = (D > b) ? (D - b) : (b - D);
            BigInteger delta = (diff * diff) + (4 * c);
            BigInteger sqrtDelta = Sqrt(delta);

            BigInteger result;
            if (D >= b)
            {
                result = (sqrtDelta + (D - b)) / 2;
            }
            else
            {
                result = (sqrtDelta - (b - D)) / 2;
            }

            return result;
        }

        private BigInteger Sqrt(BigInteger n)
        {
            if (n == 0) return 0;
            if (n > 0)
            {
                int bitLength = (int)Math.Ceiling(BigInteger.Log(n, 2));
                BigInteger root = BigInteger.One << (bitLength / 2);

                while (true)
                {
                    BigInteger nextRoot = (root + n / root) >> 1;
                    if (nextRoot >= root) return root;
                    root = nextRoot;
                }
            }
            throw new ArithmeticException("NaN");
        }
    }
}
