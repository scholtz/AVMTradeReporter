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

        /// <summary>
        /// Configures the dynamic relay discovery/ranking used when no gossip relay is statically
        /// configured (i.e. GossipWebsocketClientConfigurations is empty or its first Host is not set).
        /// </summary>
        public GossipDiscoveryConfiguration GossipDiscovery { get; set; } = new GossipDiscoveryConfiguration();

        public RedisConfiguration Redis { get; set; } = new RedisConfiguration();

        /// <summary>
        /// Pool refresh background service configuration
        /// </summary>
        public PoolRefreshConfiguration PoolRefresh { get; set; } = new PoolRefreshConfiguration();

        /// <summary>
        /// Volume update background service configuration
        /// </summary>
        public VolumeUpdateConfiguration VolumeUpdate { get; set; } = new VolumeUpdateConfiguration();

        /// <summary>
        /// Asset stats (TVL/volume/fees/APR rollup) background service configuration
        /// </summary>
        public AssetStatsConfiguration AssetStats { get; set; } = new AssetStatsConfiguration();

        /// <summary>
        /// Top assets highlight lists (Popular/Trending/Gainers/Losers) background service configuration
        /// </summary>
        public TopAssetsConfiguration TopAssets { get; set; } = new TopAssetsConfiguration();

        /// <summary>
        /// 7d hourly asset timeseries (price/TVL OHLC sparklines) background service configuration
        /// </summary>
        public AssetTimeseriesConfiguration AssetTimeseries { get; set; } = new AssetTimeseriesConfiguration();

        /// <summary>
        /// Block processing configuration
        /// </summary>
        public BlockProcessingConfiguration BlockProcessing { get; set; } = new BlockProcessingConfiguration();

        /// <summary>
        /// Stability index per asset id.
        /// Higher value means more stable / preferred base asset for USD price reporting.
        /// </summary>
        public Dictionary<ulong, int> AssetStabilityIndex { get; set; } = new();

        /// <summary>
        /// This network's primary USD-pegged stable asset (e.g. USDC). Assumed to always be
        /// worth exactly $1 and used by AggregatedPoolRepository as the reference asset every
        /// other asset's USD price is derived from (directly, when paired with it; via ALGO
        /// otherwise). Defaults to Algorand mainnet USDC so existing mainnet deployments keep
        /// working unchanged - every other network (testnet, Voi, ...) MUST override this in
        /// its own appsettings.json/ConfigMap, since asset ids are not portable across networks
        /// (e.g. Algorand testnet USDC is 10458941, Voi's Aramid USDC is 302190).
        /// </summary>
        public ulong UsdReferenceAssetId { get; set; } = 31566704UL;

        /// <summary>
        /// Trusted reference tokens (major/stable assets) used by AggregatedPoolRepository to
        /// separate "Real TVL" (only pools paired with one of these) from "Total TVL" (every
        /// pool). ALGO (asset 0) and UsdReferenceAssetId are always treated as trusted even if
        /// omitted here. Defaults to the mainnet set of major Algorand ASAs for backward
        /// compatibility - override per network, since these ids are mainnet-specific.
        /// </summary>
        public HashSet<ulong> TrustedReferenceAssetIds { get; set; } = new HashSet<ulong>
        {
            31566704UL, 1134696561UL, 2537013734UL, 1185173782UL,
            386192725UL, 1058926737UL, 2400334372UL, 760037151UL, 386195940UL,
            246516580UL, 246519683UL, 227855942UL, 2320775407UL, 887406851UL, 887648583UL,
            1241945177UL, 1241944285UL, 2320804780UL
        };

        /// <summary>
        /// Display name of this deployment's native token (asset index 0), used for the synthesized
        /// native-token asset record. Each network/environment this service indexes has its own native
        /// token and its own name for it - e.g. "Algorand" on Algorand MainNet, "Testnet Algorand" on
        /// Algorand TestNet, "Voi" on the Voi network, "Aramid" on Aramid - so this MUST be overridden
        /// per deployment in appsettings.json/ConfigMap rather than left at the default.
        /// </summary>
        public string NativeTokenName { get; set; } = "Algorand";

        /// <summary>
        /// Genesis id of the network this deployment indexes, stored on the Indexer record in
        /// Elasticsearch (e.g. "mainnet-v1.0", "testnet-v1.0", "voimain-v1.0"). Defaults to
        /// Algorand mainnet - every other network MUST override this in its own
        /// appsettings.json/ConfigMap.
        /// </summary>
        public string GenesisId { get; set; } = "mainnet-v1.0";
    }

    public class RedisConfiguration
    {
        public string ConnectionString { get; set; } = "localhost:6379";
        /// <summary>
        /// Environment namespace prepended to every Redis key/hash that is otherwise hardcoded in
        /// code (asset records "asset:{id}", top-assets summary "asset:top:summary", hourly TVL
        /// snapshots "asset:tvl:hourly:{hour}", 7d timeseries cache "asset:timeseries:7d:{id}").
        /// Defaults to "" so existing production deployments keep reading their current keys.
        /// Every non-production environment (stage/testnet, Voi, ...) MUST set its own value
        /// (e.g. "stage:") — without it, two deployments pointing at the same Redis server
        /// silently overwrite each other's data (mainnet charts showing testnet TVL/prices).
        /// Pool/aggregated-pool prefixes and pub/sub channels are configured separately below and
        /// are NOT affected by this value.
        /// </summary>
        public string EnvironmentKeyPrefix { get; set; } = "";
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

    public class VolumeUpdateConfiguration
    {
        /// <summary>
        /// Enables or disables the volume update background service
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// How often to update volumes (in seconds). Default is 60 seconds.
        /// </summary>
        public int IntervalSeconds { get; set; } = 60;
    }

    public class AssetStatsConfiguration
    {
        /// <summary>
        /// Enables or disables the asset stats background service
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// How often to recompute asset stats (in seconds). Default is 120 seconds.
        /// </summary>
        public int IntervalSeconds { get; set; } = 120;
    }

    public class TopAssetsConfiguration
    {
        /// <summary>
        /// Enables or disables the top assets background service
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// How often to recompute and re-cache the top assets lists (in seconds). Default is 300 seconds (5 minutes).
        /// </summary>
        public int IntervalSeconds { get; set; } = 300;

        /// <summary>
        /// Size of the candidate universe: assets are first ordered by real TVL descending and cut to this
        /// many entries before stable assets are excluded. Default is 150.
        /// </summary>
        public int UniverseSize { get; set; } = 150;

        /// <summary>
        /// How many assets each highlight list contains. Default is 3.
        /// </summary>
        public int ListSize { get; set; } = 3;

        /// <summary>
        /// Minimum real TVL in USD an asset must exceed to qualify for the "positive" highlight
        /// lists (Popular, Trending, Top gainers, Top liquidity gainers), keeping dust-liquidity
        /// tokens out of them. The loser lists intentionally have no floor. Default is $1000.
        /// </summary>
        public decimal MinRealTvlUsd { get; set; } = 1000m;
    }

    public class AssetTimeseriesConfiguration
    {
        /// <summary>
        /// Enables or disables the hourly asset timeseries precompute background service.
        /// The API endpoint keeps working either way (computing on demand), precompute just makes it fast.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// How many top-TVL assets get their 7d series precomputed every hour. Default is 200.
        /// </summary>
        public int UniverseSize { get; set; } = 200;
    }

    public class GossipDiscoveryConfiguration
    {
        /// <summary>
        /// Whether GossipBackgroundService should run at all when no static
        /// <see cref="AppConfiguration.GossipWebsocketClientConfigurations"/> entry is configured. Defaults to
        /// true (existing behavior, unaffected). Set to false for deployments where dynamic discovery isn't
        /// usable - notably Algorand testnet: <see cref="GossipNetwork"/> only has mainnet-family members
        /// (AlgorandMainNet, VoiMainNet, AramidMainNetBiatec, AramidMainNetAWallet), so there is no testnet
        /// value to discover with, and leaving discovery enabled without a static host previously caused
        /// non-mainnet deployments to silently connect to and gossip with Algorand mainnet relays.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Which network's relays to discover via DNS SRV when <see cref="Enabled"/> is true and no static
        /// <see cref="AppConfiguration.GossipWebsocketClientConfigurations"/> entry is configured. Defaults to
        /// <see cref="GossipNetwork.AlgorandMainNet"/>.
        /// </summary>
        public GossipNetwork Network { get; set; } = GossipNetwork.AlgorandMainNet;

        /// <summary>
        /// How many gossip relays to stay connected to once the fastest relays have been determined. Default is 10.
        /// </summary>
        public int MaxActiveRelayConnections { get; set; } = 10;

        /// <summary>
        /// How long to stay connected to all discovered relays before ranking them by delivery speed and
        /// pruning down to <see cref="MaxActiveRelayConnections"/>. Default is 120 seconds.
        /// </summary>
        public int WarmUpSeconds { get; set; } = 120;

        /// <summary>
        /// How often, once pruned, to check the active relays' health and top up the connection count back
        /// to <see cref="MaxActiveRelayConnections"/> if any relay has gone silent. Default is 300 seconds.
        /// </summary>
        public int ReEvaluationIntervalSeconds { get; set; } = 300;

        /// <summary>
        /// A connected relay that hasn't delivered any transaction within this many seconds is considered
        /// stale and is replaced with a fresh candidate from the discovered relay list. Default is 600 seconds.
        /// </summary>
        public int StaleRelaySeconds { get; set; } = 600;
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
