# AVM Trade Reporter – Developer Guide

Short overview
- ASP.NET Core (.NET 8) Web API + SignalR service for streaming Algorand DEX activity (trades, liquidity, pools, blocks)
- Ingests data from Algod/Gossip, enriches via protocol-specific processors (Pact, Tiny, Biatec)
- Indexes documents in Elasticsearch and broadcasts live updates to clients via SignalR
- Optional Redis cache for assets and pools, plus background services for continuous operation

Key capabilities
- Live SignalR hub at /biatecScanHub with recent updates and subscription-based filtering
- REST API for pool discovery and stats
- Background block processing and optional periodic pool refresh
- Swagger at /swagger using this document as the top-level description

Architecture
- Web/API: Program.cs configures controllers, Swagger, CORS, authentication, SignalR
- Hubs: Hubs/BiatecScanHub.cs exposes methods, streams events, and manages client subscriptions
- Background services
  - TradeReporterBackgroundService: processes blocks, extracts trades and liquidity, updates Elasticsearch, publishes to hub
  - GossipBackgroundService: handles gossip websocket feed (see configuration)
  - PoolRefreshBackgroundService: optional periodic re-enrichment of pools
- Repositories
  - TradeRepository: Elasticsearch storage for trades, hub broadcast
  - LiquidityRepository: storage and hub broadcast for liquidity updates
  - PoolRepository and AggregatedPoolRepository: pool storage, enrichment, Redis integration, statistics
  - AssetRepository: asset metadata via Algod with optional Redis cache
  - BlockRepository: hub publishing for new blocks
  - IndexerRepository: progress tracking (round, genesis)
- Processors (protocol-specific pool logic): PactPoolProcessor, TinyPoolProcessor, BiatecPoolProcessor
- Models: see AVMTradeReporter/Model/* (Trade, Liquidity, Pool, AggregatedPool, Block, configuration types)

Data flow (high level)
1) TradeReporterBackgroundService initializes pool cache, loads indexer progress
2) Iterates blocks from Algod (with optional fallbacks), processes transactions
3) Detects trades/liquidity/pool updates, enriches pools using protocol processors when needed
4) Stores documents in Elasticsearch (data stream/index per type)
5) Publishes recent updates to SignalR hub for clients

Configuration
The app reads AppConfiguration and related sections from appsettings.json (or environment-specific variants).

AppConfiguration
- IndexerId: string unique identifier for this indexer instance
- DelayMs: optional processing delay in milliseconds
- Direction: "+" or "-" to move forward or backward through rounds
- StartRound, MinRound, MaxRound: optional round bounds
- Algod, Algod2, Algod3: AlgodConfiguration endpoints (Host, ApiKey, Header)
- Elastic: ElasticConfiguration (Host, ApiKey)
- GossipWebsocketClientConfigurations: list of websocket peers (if used by gossip service)
- Redis: RedisConfiguration
  - ConnectionString: e.g., localhost:6379
  - KeyPrefix: default avmtrade:pools:
  - Enabled: true/false
  - DatabaseId: integer DB id
- PoolRefresh: PoolRefreshConfiguration
  - Enabled: enable background refresh
  - IntervalHours: cadence of full refresh
  - DelayBetweenPoolsSeconds: throttle between pool refreshes
  - InitialDelayMinutes: startup delay before first cycle

CORS
- Configure allowed origins under the Cors section; the app will fail startup if not provided

Authentication
- Uses AlgorandAuthenticationV2 per Program.cs
- Swagger defines an API key scheme named oauth2 (ARC-0014 Algorand authentication transaction transmitted in Authorization header)
- The SignalR pipeline moves access_token from query string to Authorization header for compatibility

Elasticsearch
- Client configured with ApiKey authentication and default mappings
- TradeRepository creates a template for trades-* as a data stream
- Mappings configured in Program.cs for:
  - Trade -> index "trades"
  - Liquidity -> index "liquidity"
  - AggregatedPool -> index "liquidity" (aggregates)
  - Pool -> index "pools"
  - Indexer -> index "indexers"

SignalR hub (/biatecScanHub)
Client methods to call
- TestConnection(): returns connection/auth context
- Subscribe(filter): authorized; sets a user-specific filter string
- Unsubscribe(): authorized; clears subscription

Server events you can receive
- Block: latest block metadata
- FilteredTradeUpdated: trade that passed your subscription filter
- FilteredLiquidityUpdated: liquidity event that passed your filter
- PoolUpdated: pool metadata update
- AggregatedPoolUpdated: aggregated pool update
- TestMessage: from test controller broadcast endpoint
- Subscribed / Unsubscribed: subscription acknowledgements

Filter format examples
- protocol:Biatec
- asset:31566704
- trader:ALGORAND_ACCOUNT
- pool:123456
- pooladdress:ALGORAND_ADDRESS
- state:TxPool or Confirmed

Typical JS client setup (pseudo)
- new signalR.HubConnectionBuilder().withUrl("/biatecScanHub?access_token=ALG_AUTH_TOKEN").build()
- await connection.start()
- await connection.invoke("Subscribe", "protocol:Biatec")
- connection.on("FilteredTradeUpdated", payload => { /* handle */ })

REST API
- GET /api/pool
  - Query: assetIdA, assetIdB, protocol (Pact|Tiny|Biatec), size (default 100)
  - Returns list of pools (optionally filtered)
- GET /api/pool/{poolAddress}
  - Returns details for a specific pool address
- GET /api/pool/stats
  - Optional: assetIdA, assetIdB
- Test utilities (development)
  - GET /api/signalr/auth-test
  - GET /api/signalr/auth-test-authorized (requires auth)
  - POST /api/signalr/test-broadcast (requires auth)
  - POST /api/signalr/test-trade (requires auth)

Pool enrichment and refresh
- Pools updated from trades or liquidity may miss metadata (e.g., approval program hash)
- PoolRepository can auto-enrich using protocol processors, with fallback between Tiny/Pact where appropriate
- Optional PoolRefreshBackgroundService periodically refreshes all pools
- See POOL_AUTO_ENRICHMENT.md and POOL_REFRESH_CONFIGURATION.md for deeper details

Running locally
Prerequisites
- .NET 8 SDK
- Elasticsearch endpoint and API key
- Algod endpoint(s) and API key(s)
- Redis (optional but recommended)

Steps
- Configure appsettings.Development.json with AppConfiguration, Cors, AlgorandAuthentication
- dotnet run in AVMTradeReporter
- Open /swagger to explore the API; this document appears as the description

Notes
- Program.cs includes doc/documentation.xml for Swagger XML comments; enable XML docs in project if desired
- DockerDefaultTargetOS is Linux; containerization supported via standard ASP.NET Core tooling
- Tests live in AVMTradeReporterTests (NUnit)

Troubleshooting
- If SignalR auth fails, ensure access_token query string contains the Algorand auth value and that CORS allows your origin
- If startup fails due to CORS, add origins under Cors in configuration
- If Elasticsearch calls fail, verify Host/ApiKey and network access
- If Algod calls fail, verify Algod Host/ApiKey/Header and optional fallbacks Algod2/Algod3
- Logs include detailed diagnostics for SignalR, authentication, and background processing
