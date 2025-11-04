using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;

namespace AVMTradeReporter.Model
{
    public interface ITradeService
    {
        public Task RegisterTrade(Trade trade, CancellationToken cancellationToken);
    }
}
