using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services.ScamRating;

namespace AVMTradeReporterTests.Services.ScamRating
{
    /// <summary>
    /// Pure rules derived from <see cref="Pool.ScamRating"/> - no I/O.
    /// </summary>
    public class ScamRatingPolicyTests
    {
        private static AVMTradeReporter.Models.Data.Pool MakePool(int scamRating) => new()
        {
            PoolAddress = "POOL",
            PoolAppId = 1,
            Protocol = DEXProtocol.Biatec,
            AMMType = AMMType.ConcentratedLiquidityAMM,
            ScamRating = scamRating,
            A = 1_000_000_000,
            B = 2_000_000_000,
            AF = 10,
            BF = 20,
            StableA = 30,
            StableB = 40,
            L = 5,
        };

        [Test]
        public void ScamRating_DefaultsToZero()
        {
            var pool = new AVMTradeReporter.Models.Data.Pool();
            Assert.That(pool.ScamRating, Is.EqualTo(0));
        }

        [Test]
        public void Reverse_CopiesScamRating()
        {
            var pool = MakePool(42);
            Assert.That(pool.Reverse().ScamRating, Is.EqualTo(42));
        }

        [TestCase(0, false)]
        [TestCase(5, false)]
        [TestCase(80, false)]
        [TestCase(81, true)]
        [TestCase(100, true)]
        public void ShouldZeroBalances_OnlyAboveThreshold(int rating, bool expected)
        {
            Assert.That(ScamRatingPolicy.ShouldZeroBalances(rating), Is.EqualTo(expected));
        }

        [Test]
        public void ApplyBalanceRule_RatingAtOrBelow80_LeavesBalancesUntouched()
        {
            var pool = MakePool(80);

            var changed = ScamRatingPolicy.ApplyBalanceRule(pool);

            Assert.Multiple(() =>
            {
                Assert.That(changed, Is.False);
                Assert.That(pool.A, Is.EqualTo(1_000_000_000UL));
                Assert.That(pool.B, Is.EqualTo(2_000_000_000UL));
                Assert.That(pool.AF, Is.EqualTo(10UL));
                Assert.That(pool.BF, Is.EqualTo(20UL));
                Assert.That(pool.StableA, Is.EqualTo(30UL));
                Assert.That(pool.StableB, Is.EqualTo(40UL));
            });
        }

        [Test]
        public void ApplyBalanceRule_RatingAbove80_ZeroesAssetAAndAssetBBalances()
        {
            var pool = MakePool(100);

            var changed = ScamRatingPolicy.ApplyBalanceRule(pool);

            Assert.Multiple(() =>
            {
                Assert.That(changed, Is.True);
                Assert.That(pool.A, Is.EqualTo(0UL));
                Assert.That(pool.B, Is.EqualTo(0UL));
                Assert.That(pool.AF, Is.EqualTo(0UL));
                Assert.That(pool.BF, Is.EqualTo(0UL));
                Assert.That(pool.StableA, Is.EqualTo(0UL));
                Assert.That(pool.StableB, Is.EqualTo(0UL));
                Assert.That(pool.RealAmountA, Is.EqualTo(0m));
                Assert.That(pool.RealAmountB, Is.EqualTo(0m));
                Assert.That(pool.VirtualAmountAForPrice, Is.EqualTo(0m));
                Assert.That(pool.VirtualAmountBForPrice, Is.EqualTo(0m));
                // liquidity token supply / identity fields are not balances and stay as they were
                Assert.That(pool.L, Is.EqualTo(5UL));
                Assert.That(pool.ScamRating, Is.EqualTo(100));
            });
        }

        [Test]
        public void ApplyBalanceRule_AlreadyZeroed_ReportsNoChange()
        {
            var pool = MakePool(100);
            ScamRatingPolicy.ApplyBalanceRule(pool);

            Assert.That(ScamRatingPolicy.ApplyBalanceRule(pool), Is.False);
        }

        [TestCase(-5, 0)]
        [TestCase(0, 0)]
        [TestCase(55, 55)]
        [TestCase(100, 100)]
        [TestCase(250, 100)]
        public void Clamp_KeepsRatingWithin0And100(int input, int expected)
        {
            Assert.That(ScamRatingPolicy.Clamp(input), Is.EqualTo(expected));
        }
    }
}
