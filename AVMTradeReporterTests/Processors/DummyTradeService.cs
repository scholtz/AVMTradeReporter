using AVMTradeReporter.Model;
using AVMTradeReporter.Models.Data;

namespace AVMTradeReporterTests.Processors
{
    public class DummyTradeService : ITradeService
    {
        public List<Trade> trades = new List<Trade>();
        public async Task RegisterTrade(Trade trade, CancellationToken cancellationToken)
        {
            trades.Add(trade);
            await Task.Delay(1);
        }
    }
}
