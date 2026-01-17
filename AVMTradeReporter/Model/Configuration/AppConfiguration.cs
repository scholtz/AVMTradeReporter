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
        /// Fallback algod configuration
        /// </summary>
        public AlgodConfiguration Algod2 { get; set; } = new AlgodConfiguration();
        /// <summary>
        /// Fallback algod configuration
        /// </summary>
        public AlgodConfiguration Algod3 { get; set; } = new AlgodConfiguration();

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

        /// <summary>
        /// Block processing configuration
        /// </summary>
        public BlockProcessingConfiguration BlockProcessing { get; set; } = new BlockProcessingConfiguration();

        /// <summary>
        /// Stability index per asset id.
        /// Higher value means more stable / preferred base asset for USD price reporting.
        /// </summary>
        public Dictionary<ulong, int> AssetStabilityIndex { get; set; } = new();
    }

    public class RedisConfiguration
    {
        public string ConnectionString { get; set; } = "localhost:6379";
        public string KeyPrefix { get; set; } = "avmtrade:pools:";
        public string AggregatedPoolKeyPrefix { get; set; } = "avmtrade:aggregatedpools:"; // new prefix for persisted aggregated pools
        public bool Enabled { get; set; } = true;
        public int DatabaseId { get; set; } = 0;
        public string PoolUpdateChannel { get; set; } = "avmtrade:pool:updates";
        public string AggregatedPoolUpdateChannel { get; set; } = "avmtrade:aggregatedpool:updates";
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

    public class BlockProcessingConfiguration
    {
        /// <summary>
        /// Enable asynchronous block processing. When true, blocks are processed concurrently.
        /// </summary>
        public bool EnableAsyncProcessing { get; set; } = true;

        /// <summary>
        /// Maximum number of concurrent block processing tasks. Default is 3.
        /// </summary>
        public int MaxConcurrentTasks { get; set; } = 3;

        /// <summary>
        /// Memory threshold in MB above which async processing is disabled. Default is 1024 MB (1 GB).
        /// </summary>
        public long MemoryThresholdMB { get; set; } = 1024;

        /// <summary>
        /// How often to check memory usage in milliseconds. Default is 5000 ms (5 seconds).
        /// </summary>
        public int MemoryCheckIntervalMs { get; set; } = 5000;
    }
}
