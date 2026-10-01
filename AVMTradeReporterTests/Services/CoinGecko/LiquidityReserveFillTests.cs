using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Services;
using PoolModel = AVMTradeReporter.Models.Data.Pool;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    /// <summary>
    /// A reserve the liquidity event did not touch is absent from the global-state delta and parsed as 0. For a deposit
    /// that means "unchanged" (a deposit cannot empty a side), so the pool cache's value from before the event is used.
    /// </summary>
    public class LiquidityReserveFillTests
    {
        private static Liquidity Deposit(ulong a, ulong b) => new() { Direction = LiquidityDirection.DepositLiquidity, A = a, B = b };
        private static PoolModel Before(ulong a, ulong b) => new() { A = a, B = b };

        [Test]
        public void SingleSidedDeposit_GetsTheUntouchedReserveFromThePool()
        {
            var deposit = Deposit(5_000, 0);
            TradeReporterBackgroundService.FillUnchangedReservesOfDeposit(deposit, Before(4_000, 9_000));
            Assert.That((deposit.A, deposit.B), Is.EqualTo((5_000UL, 9_000UL)));
        }

        [Test]
        public void KnownReserves_AreLeftAlone()
        {
            var deposit = Deposit(5_000, 7_000);
            TradeReporterBackgroundService.FillUnchangedReservesOfDeposit(deposit, Before(4_000, 9_000));
            Assert.That((deposit.A, deposit.B), Is.EqualTo((5_000UL, 7_000UL)));
        }

        [Test]
        public void Withdrawal_IsNeverFilled_ZeroMayBeARealDrain()
        {
            var exit = new Liquidity { Direction = LiquidityDirection.WithdrawLiquidity, A = 0, B = 0 };
            TradeReporterBackgroundService.FillUnchangedReservesOfDeposit(exit, Before(4_000, 9_000));
            Assert.That((exit.A, exit.B), Is.EqualTo((0UL, 0UL)));
        }

        [Test]
        public void UnknownPoolOrEmptyPoolSide_LeavesTheZero()
        {
            var deposit = Deposit(5_000, 0);
            TradeReporterBackgroundService.FillUnchangedReservesOfDeposit(deposit, null);
            Assert.That(deposit.B, Is.Zero);
            TradeReporterBackgroundService.FillUnchangedReservesOfDeposit(deposit, Before(4_000, 0));
            Assert.That(deposit.B, Is.Zero, "the pool side really was empty - a brand new pool's first deposit");
        }
    }
}
