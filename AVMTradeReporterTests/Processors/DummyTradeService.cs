using AVMTradeReporter.Model;
using AVMTradeReporter.Models.Data;

namespace AVMTradeReporterTests.Processors
{
    public class DummyTradeService : ITradeService
    {
        public List<Trade> trades = new List<Trade>();

        /// <summary>
        /// (TxnIndex, EventIndex) the block processor assigned to each registered trade, in registration order.
        /// The fields are cleared on the stored trade so the legacy exact-JSON assertions of the processor tests
        /// keep describing only what the protocol processors themselves produce.
        /// </summary>
        public List<(ulong? TxnIndex, uint? EventIndex)> positions = new List<(ulong?, uint?)>();

        public async Task RegisterTrade(Trade trade, CancellationToken cancellationToken)
        {
            positions.Add((trade.TxnIndex, trade.EventIndex));
            trade.TxnIndex = null;
            trade.EventIndex = null;
            trades.Add(trade);
            await Task.Delay(1);
        }
    }
}
