using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;

namespace AVMTradeReporter.Model
{
    public interface ILiquidityService
    {
        public Task RegisterLiquidity(Liquidity liquidityUpdate, CancellationToken cancellationToken);
    }
}
