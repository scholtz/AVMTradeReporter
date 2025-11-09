namespace AVMTradeReporter.Processors.Pool
{
    public interface IPoolProcessor
    {
        public Task<AVMTradeReporter.Models.Data.Pool> LoadPoolAsync(string address, ulong appId);
    }
}
