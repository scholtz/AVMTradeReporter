using AVMTradeReporter.Model;
using AVMTradeReporter.Model.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests.Processors
{
    public class DummyTradeService : ITradeService
    {
        public List<Trade> trades = new List<Trade>();
        public async Task RegisterTrade(Trade trade, CancellationToken cancellationToken)
        {
            trades.Add(trade);
        }
    }
}
