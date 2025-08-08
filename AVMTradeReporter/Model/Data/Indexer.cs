namespace AVMTradeReporter.Model.Data
{
    public class Indexer
    {
        public string Id { get; set; } = string.Empty;
        public ulong Round { get; set; }
        public string GenesisId { get; set; } = string.Empty;
        public DateTimeOffset Updated { get; set; }
    }
}
