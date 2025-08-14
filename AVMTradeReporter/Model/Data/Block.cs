namespace AVMTradeReporter.Model.Data
{
    public class Block
    {
        public ulong Round { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string GenesisId { get; set; } = string.Empty;
        public int Transactions { get; set; }
        public ulong TotalTransactions { get; set; }
        public static Block FromAlgorandBlock(Algorand.Algod.Model.Block algoBlock)
        {
            return new Block
            {
                Round = algoBlock.Round ?? 0,
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(algoBlock.Timestamp ?? 0)),
                GenesisId = algoBlock.GenesisId ?? "",
                Transactions = algoBlock.Transactions?.Count ?? 0,
                TotalTransactions = algoBlock.TxnCounter ?? 0,
            };
        }
    }
}
