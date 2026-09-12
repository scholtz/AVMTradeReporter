using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;

namespace AVMTradeReporter.Services.ScamRating
{
    /// <summary>
    /// Pure, side-effect free rules that derive from a pool's <see cref="Pool.ScamRating"/>.
    /// Kept separate from <see cref="ScamRatingService"/> (which computes the rating and needs I/O) so the
    /// consequences of a rating can be unit tested without any network or repository.
    /// </summary>
    public static class ScamRatingPolicy
    {
        /// <summary>
        /// Rating of a known scammer pool that nobody should trust.
        /// </summary>
        public const int KnownScamRating = 100;

        /// <summary>
        /// Ratings strictly above this threshold get their asset A / asset B balances forced to 0.
        /// </summary>
        public const int ZeroBalancesThreshold = 80;

        /// <summary>
        /// Maximum value of a rating (ratings are clamped to 0..100).
        /// </summary>
        public const int MaxRating = 100;

        public static int Clamp(int rating) => Math.Clamp(rating, 0, MaxRating);

        /// <summary>
        /// True when the pool's balances must be reported as 0 because of its scam rating.
        /// </summary>
        public static bool ShouldZeroBalances(int scamRating) => scamRating > ZeroBalancesThreshold;

        /// <summary>
        /// Applies the protocol rule: a pool rated above <see cref="ZeroBalancesThreshold"/> is
        /// re-labelled <see cref="DEXProtocol.Scam"/> so it is never presented as (or processed like) the
        /// protocol it imitates. The label is sticky - see <see cref="IsProtocolLocked"/>.
        /// Returns true when the protocol was changed.
        /// </summary>
        public static bool ApplyProtocolRule(Pool pool)
        {
            if (!ShouldZeroBalances(pool.ScamRating)) return false;
            if (pool.Protocol == DEXProtocol.Scam) return false;
            pool.Protocol = DEXProtocol.Scam;
            return true;
        }

        /// <summary>
        /// True when the pool's protocol must not be rewritten by an incoming trade / liquidity event /
        /// pool processor result (the pool is already labelled <see cref="DEXProtocol.Scam"/>).
        /// </summary>
        public static bool IsProtocolLocked(Pool pool) => pool.Protocol == DEXProtocol.Scam;

        /// <summary>
        /// Applies every consequence of the pool's current rating (protocol label + zero balances).
        /// Returns true when anything changed.
        /// </summary>
        public static bool Enforce(Pool pool)
        {
            var protocolChanged = ApplyProtocolRule(pool);
            var balancesChanged = ApplyBalanceRule(pool);
            return protocolChanged || balancesChanged;
        }

        /// <summary>
        /// Applies the balance rule to the pool: when its scam rating exceeds
        /// <see cref="ZeroBalancesThreshold"/>, every balance field that feeds
        /// <see cref="Pool.RealAmountA"/> / <see cref="Pool.RealAmountB"/> (A, B, the protocol fee
        /// balances AF/BF and the stable-swap balances) is set to 0, so the pool contributes no liquidity,
        /// TVL or price anywhere downstream. Returns true when any field was changed.
        /// </summary>
        public static bool ApplyBalanceRule(Pool pool)
        {
            if (!ShouldZeroBalances(pool.ScamRating)) return false;

            var changed = false;
            if (pool.A != 0) { pool.A = 0; changed = true; }
            if (pool.B != 0) { pool.B = 0; changed = true; }
            if (pool.AF.HasValue && pool.AF != 0) { pool.AF = 0; changed = true; }
            if (pool.BF.HasValue && pool.BF != 0) { pool.BF = 0; changed = true; }
            if (pool.StableA.HasValue && pool.StableA != 0) { pool.StableA = 0; changed = true; }
            if (pool.StableB.HasValue && pool.StableB != 0) { pool.StableB = 0; changed = true; }
            return changed;
        }
    }
}
