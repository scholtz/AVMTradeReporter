using Algorand.Gossip;
using Elastic.Clients.Elasticsearch.Inference;
using AVMTradeReporter.Model.Configuration;

namespace AVMTradeReporter.Model.Configuration
{
    public class AppConfiguration
    {
        /// <summary>
        /// Gets or sets the unique identifier for the indexer.
        /// </summary>
        public string IndexerId { get; set; } = String.Empty;
        public int? DelayMs { get; set; } = 0;
        /// <summary>
        /// + or -
        /// </summary>
        public string Direction { get; set; } = "+";
        public ulong? StartRound { get; set; } = null;
        public ulong? MinRound { get; set; } = null;
        public ulong? MaxRound { get; set; } = null;
        /// <summary>
        /// AVM Algod configuration
        /// </summary>
        public AlgodConfiguration Algod { get; set; } = new AlgodConfiguration();

        /// <summary>
        /// Elasticsearch configuration
        /// </summary>
        public ElasticConfiguration Elastic { get; set; } = new ElasticConfiguration();
        /// <summary>
        /// Config of the gossip algod nodes
        /// </summary>
        public List<GossipWebsocketClientConfiguration> GossipWebsocketClientConfigurations { get; set; } = new();

        public RedisConfiguration Redis { get; set; } = new RedisConfiguration();

        /// <summary>
        /// Pool refresh background service configuration
        /// </summary>
        public PoolRefreshConfiguration PoolRefresh { get; set; } = new PoolRefreshConfiguration();
    }

    public class RedisConfiguration
    {
        public string ConnectionString { get; set; } = "localhost:6379";
        public string KeyPrefix { get; set; } = "avmtrade:pools:";
        public bool Enabled { get; set; } = true;
        public int DatabaseId { get; set; } = 0;
    }

    public class PoolRefreshConfiguration
    {
        /// <summary>
        /// Enables or disables the pool refresh background service
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// How often to refresh all pools (in hours). Default is 24 hours (once per day).
        /// </summary>
        public int IntervalHours { get; set; } = 24;

        /// <summary>
        /// Delay between processing each pool (in seconds). Default is 5 seconds.
        /// </summary>
        public int DelayBetweenPoolsSeconds { get; set; } = 5;

        /// <summary>
        /// Initial delay before starting the first refresh (in minutes). Default is 1 minute.
        /// </summary>
        public int InitialDelayMinutes { get; set; } = 1;
    }
}
