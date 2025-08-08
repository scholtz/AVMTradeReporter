using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests.Processors
{
    public class DummyLiquidityService : ILiquidityService
    {
        public List<Liquidity> list = new List<Liquidity>();

        public async Task RegisterLiquidity(Liquidity liquidityUpdate, CancellationToken cancellationToken)
        {
            list.Add(liquidityUpdate);
            await Task.Delay(1);
        }
    }
}
