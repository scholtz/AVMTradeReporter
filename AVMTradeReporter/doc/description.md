# AVM Trade Reporter – Developer Guide

## Authentication (ARC-0014)

Most endpoints in this API require **[ARC-0014](https://github.com/algorandfoundation/ARCs/issues/42)**
authentication ("Algorand transaction signature as authentication"). Instead of a username/password or
long-lived API key, the client signs a zero-fee, unbroadcast `NoOp`/payment-shaped Algorand transaction
whose `Note` field encodes a realm and expiry, then sends the signed transaction as the `Authorization`
header. The server verifies the signature and transaction validity window against Algorand node
transaction params — no signed transaction is ever submitted to the chain, it is purely a proof of key
ownership.

- Realm for this API: `BiatecScan#ARC14`
- Header: `Authorization: SigTx <base64-encoded-signed-transaction>` (produced by the libraries below)
- The signed transaction's validity window (`firstValid`/`lastValid`) doubles as the token's expiry —
  there is no separate refresh step; the client just signs a new transaction with a fresh validity
  window when the old one expires
- Endpoints marked **"no authentication required"** below are intentionally public and accept
  unauthenticated requests

### Recommended client libraries

- **JavaScript/TypeScript**: [`arc14`](https://www.npmjs.com/package/arc14) (builds and signs the
  ARC-14 transaction, produces the `Authorization` header value) together with
  [`arc76`](https://www.npmjs.com/package/arc76) (deterministic Algorand keypair derivation, if the
  client needs a stable identity without a wallet) and [`algosdk`](https://www.npmjs.com/package/algosdk)
  for transaction params/signing primitives. Example:
  ```ts
  import algosdk from "algosdk";
  import { makeArc14AuthHeader, makeArc14TxWithSuggestedParams } from "arc14";

  const tx = await makeArc14TxWithSuggestedParams("BiatecScan#ARC14", account.addr.toString(), suggestedParams);
  const signed = tx.signTxn(account.sk);
  const authorizationHeader = makeArc14AuthHeader(signed);
  // send as: Authorization: <authorizationHeader>
  ```
- **.NET**: server-side verification here uses the
  [`AlgorandAuthentication`](https://www.nuget.org/packages/AlgorandAuthentication) NuGet package
  (see `AlgorandAuthenticationHandlerV2` in `Program.cs`); .NET clients can build the equivalent
  ARC-14 signed transaction directly with the [`Algorand4`](https://www.nuget.org/packages/Algorand4)
  SDK (construct a transaction with `Note` set to the realm, sign it with the account's private key,
  base64-encode it into the `Authorization` header) — there is no separate client-only package needed
  since the same SDK used to sign is already an `AVMTradeReporter` dependency.

Short overview
- ASP.NET Core (.NET 10) Web API + SignalR service for streaming Algorand DEX activity (trades, liquidity, pools, blocks)
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
  - StatsRepository: read-only Elasticsearch sum aggregations for DefiLlama DEX stats export
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
- GossipWebsocketClientConfigurations: list of websocket peers (if used by gossip service). If the first entry's Host is empty (e.g. left unset via `AppConfiguration__GossipWebsocketClientConfigurations__0__Host`), the gossip service instead discovers all relays via DNS SRV, connects to all of them, and keeps only the fastest ones connected per GossipDiscovery.
- GossipDiscovery: dynamic relay discovery/ranking used when no gossip relay is statically configured
  - MaxActiveRelayConnections: how many relays to stay connected to once ranked (default 10)
  - WarmUpSeconds: how long to race all discovered relays before pruning down (default 120)
  - ReEvaluationIntervalSeconds: how often to check active relays' health once pruned (default 300)
  - StaleRelaySeconds: a connected relay silent for longer than this is replaced with a fresh candidate (default 600)
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
- Uses AlgorandAuthenticationV2 per Program.cs; see the "Authentication (ARC-0014)" section at the top of this document for how to generate a token as a client
- Swagger defines an API key scheme named arc14 (ARC-0014 Algorand authentication transaction transmitted in Authorization header)
- The SignalR pipeline moves access_token from query string to Authorization header for compatibility
- All REST endpoints require authentication ([Authorize]) except the following, which are intentionally public:
  - GET /api/asset (asset list/lookup; the TradingView charting widget resolves asset id -> ticker via this endpoint with a plain unauthenticated fetch and has no ARC-14 signing capability - see AVMTradeReporterTests/Controllers/AssetControllerAuthorizationTests.cs)
  - GET /api/asset/image/{assetId} (asset image, embedded directly as an <img> src)
  - GET /api/Gossip/status (relay connectivity health check)
  - GET /api/Stats/dex (DefiLlama DEX stats adapter integration)
  - GET /api/OHLC/* (TradingView UDF charting datafeed: config, time, symbols, symbol_info, search, marks, timescale_marks, quotes, history)

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
- GET /api/stats/dex (no authentication required, designed for DefiLlama adapters)
  - Query: dex (Biatec|Pact|Tiny), timestamp (DateTimeOffset, inclusive window start)
  - Returns aggregated 24-hour statistics for the given DEX protocol over [timestamp, timestamp+1day)
  - Only confirmed trades are included in the aggregation
  - Response fields: protocol, from, to, volumeUSD, feesUSD, feesLPUSD, feesProtocolUSD
  - Powered by Elasticsearch sum aggregations on valueUSD, feesUSD, feesUSDProvider, feesUSDProtocol
  - Example: GET /api/stats/dex?dex=Biatec&timestamp=2024-01-15T00:00:00Z
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
- .NET 10 SDK
- Elasticsearch endpoint and API key
- Algod endpoint(s) and API key(s)
- Redis (optional but recommended)

Steps
- Configure appsettings.Development.json with AppConfiguration, Cors, AlgorandAuthentication
- dotnet run in AVMTradeReporter
- Open /swagger to explore the API; this document appears as the description

Notes
- GenerateDocumentationFile is enabled in AVMTradeReporter.csproj and Program.cs wires the generated
  AVMTradeReporter.xml into Swagger via IncludeXmlComments, so /// XML doc comments on controllers and
  actions are surfaced as operation summaries/descriptions in Swagger UI
- Swagger is served as OpenAPI 3.1 (app.UseSwagger sets OpenApiVersion = OpenApi3_1 in Program.cs)
- DockerDefaultTargetOS is Linux; containerization supported via standard ASP.NET Core tooling
- Tests live in AVMTradeReporterTests (NUnit)

Troubleshooting
- If SignalR auth fails, ensure access_token query string contains the Algorand auth value and that CORS allows your origin
- If startup fails due to CORS, add origins under Cors in configuration
- If Elasticsearch calls fail, verify Host/ApiKey and network access
- If Algod calls fail, verify Algod Host/ApiKey/Header and optional fallbacks Algod2/Algod3
- Logs include detailed diagnostics for SignalR, authentication, and background processing
