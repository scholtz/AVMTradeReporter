namespace AVMTradeReporter.Processors.Pool
{
    public interface IPoolProcessor
    {
        public Task<AVMTradeReporter.Model.Data.Pool> LoadPoolAsync(string address, ulong appId);
    }
}
