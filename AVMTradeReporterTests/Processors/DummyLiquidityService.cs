using AVMTradeReporter.Model;
using AVMTradeReporter.Models.Data;
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

        /// <summary>See <see cref="DummyTradeService.positions"/>.</summary>
        public List<(ulong? TxnIndex, uint? EventIndex)> positions = new List<(ulong?, uint?)>();

        public async Task RegisterLiquidity(Liquidity liquidityUpdate, CancellationToken cancellationToken)
        {
            positions.Add((liquidityUpdate.TxnIndex, liquidityUpdate.EventIndex));
            liquidityUpdate.TxnIndex = null;
            liquidityUpdate.EventIndex = null;
            list.Add(liquidityUpdate);
            await Task.Delay(1);
        }
    }
}
