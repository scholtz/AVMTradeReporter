namespace AVMTradeReporter.Models.Data
{
    public class Indexer
    {
        public string Id { get; set; } = string.Empty;
        public ulong Round { get; set; }
        public string GenesisId { get; set; } = string.Empty;
        public DateTimeOffset Updated { get; set; }

        /// <summary>
        /// Highest round up to which EVERY earlier block's trades and liquidity events are stored (contiguous). <see cref="Round"/>
        /// is persisted when a block task starts, so after a crash it can sit above blocks that were never stored; the forward
        /// indexer restarts at <c>StoredThrough + 1</c> to re-process them (stores are idempotent). Null on documents written
        /// before this field existed.
        /// </summary>
        public ulong? StoredThrough { get; set; }
    }
}
