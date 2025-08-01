namespace AVMTradeReporter.Model.Data
{
    public class Indexer
    {
        public string Id { get; set; }
        public ulong Round { get; set; }
        public string GenesisId { get; set; }
        public DateTimeOffset Updated { get; set; }
    }
}
