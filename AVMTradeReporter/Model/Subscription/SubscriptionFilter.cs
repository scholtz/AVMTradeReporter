namespace AVMTradeReporter.Model.Subscription
{
    public class SubscriptionFilter
    {
        public bool RecentBlocks { get; set; } = false;
        public bool RecentTrades { get; set; } = false;
        public bool RecentLiquidity { get; set; } = false;
        public bool RecentPool { get; set; } = false;
        public bool RecentAggregatedPool { get; set; } = false;

        public bool MainAggregatedPools { get; set; } = false;

        public List<string> PoolsAddresses { get; set; } = new();
        public List<string> AggregatedPoolsIds { get; set; } = new();
    }
}
