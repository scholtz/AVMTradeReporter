namespace AVMTradeReporter.Model.Subscription
{
    public class SubscriptionFilter
    {
        public bool RecentBlocks { get; set; } = false;
        public bool RecentTrades { get; set; } = false;
        public bool RecentLiquidity { get; set; } = false;
        public bool RecentPool { get; set; } = false;
        public bool RecentAggregatedPool { get; set; } = false;
        public bool RecentAssets { get; set; } = false;

        public bool MainAggregatedPools { get; set; } = false;

        public HashSet<string> PoolsAddresses { get; set; } = new();
        public HashSet<string> AggregatedPoolsIds { get; set; } = new();
        public HashSet<string> AssetIds { get; set; } = new();
    }
}
