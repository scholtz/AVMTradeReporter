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
    }

    public class RedisConfiguration
    {
        public string ConnectionString { get; set; } = "localhost:6379";
        public string KeyPrefix { get; set; } = "avmtrade:pools:";
        public bool Enabled { get; set; } = true;
        public int DatabaseId { get; set; } = 0;
    }
}
