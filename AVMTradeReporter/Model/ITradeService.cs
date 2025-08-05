using AVMTradeReporter.Model.Data;

namespace AVMTradeReporter.Model
{
    public interface ITradeService
    {
        public Task RegisterTrade(Trade trade, CancellationToken cancellationToken);
    }
}
