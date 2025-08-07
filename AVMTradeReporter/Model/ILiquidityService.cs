using AVMTradeReporter.Model.Data;

namespace AVMTradeReporter.Model
{
    public interface ILiquidityService
    {
        public Task RegisterLiquidity(Liquidity liquidityUpdate, CancellationToken cancellationToken);
    }
}
