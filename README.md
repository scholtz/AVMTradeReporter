# AVM Trade Reporter

ASP.NET Core (.NET 10) Web API + SignalR service that indexes Algorand DEX
activity — trades, liquidity events, pools, and blocks — across **Pact**,
**TinyMan**, and **Biatec** and streams it live to subscribers. It reads from
Algod (block/algod REST API) and the gossip mempool feed, enriches
protocol-specific pool data, stores everything in Elasticsearch, optionally
caches in Redis, and pushes real-time updates over SignalR. It also serves a
TradingView-UDF-compatible OHLC datafeed for the charting widget.

Repo: [`scholtz/AVMTradeReporter`](https://github.com/scholtz/AVMTradeReporter)
(default/working branch is **`master`**, not `main`).

## Where it lives

One Docker image (`scholtz2/avm-trade-reporter`) runs three independent
deployments in the same Kubernetes namespace (`biatec-scan`), kept apart
purely by distinct resource names/ConfigMaps/Secrets — network specifics
(Algod host, gossip network, USD reference asset, Redis key prefix) all come
from the per-environment ConfigMap/Secret, not the image.

| Environment | Network | API / Swagger | Deploys via |
|---|---|---|---|
| Production | Algorand mainnet | `https://api.algorand.scan.biatec.io/swagger` | `promote-production.yml` (manual) |
| Stage | Algorand testnet | `https://api.testnet.scan.biatec.io/swagger` | `deploy.yml`, automatic on every push to `master` |
| Production (Voi) | Voi mainnet | `https://api.voi.scan.biatec.io/swagger` | `promote-production.yml` (manual, `network=voi`) |

See [`docs/STAGE_ENVIRONMENT.md`](docs/STAGE_ENVIRONMENT.md) for the full
stage-vs-production isolation matrix and
[`docs/CICD_GITHUB_ACTIONS.md`](docs/CICD_GITHUB_ACTIONS.md) for the deploy
workflows in detail. K8s manifests live under `k8s/main`, `k8s/stage`,
`k8s/voi`.

## Project structure

| Project | Purpose |
|---|---|
| [`AVMTradeReporter`](AVMTradeReporter/) | Main Web API + SignalR service — everything described above (`Controllers/`, `Hubs/`, `Repository/`, `Processors/Pool/`, `Services/`, background services) |
| [`AVMTradeReporter.Models`](AVMTradeReporter.Models/README.md) | Standalone shared data-model library (`Pool`, `Trade`, `Liquidity`, `AggregatedPool`, `OHLC`, `AssetStat`, `BiatecAsset`, ...), published independently to [NuGet.org](https://www.nuget.org/) so third-party consumers can depend on the DTOs without the whole API — see [`NUGET_PUBLISHING.md`](NUGET_PUBLISHING.md) |
| [`AVMTradeReporter.Subscriber`](AVMTradeReporter.Subscriber/README.md) | Sample console app showing how to subscribe to the API's Redis PubSub channels (`avmtrade:pool:updates`, `avmtrade:aggregatedpool:updates`) for pool updates outside SignalR |
| `AVMTradeReporterTests` | NUnit test suite. A meaningful chunk of tests need a live Algod/network connection and are expected to fail offline — see [Testing](#testing) below |

## Use cases / REST API

Every endpoint is documented with XML doc comments surfaced in Swagger UI
(`/swagger` on any environment above) — this is a summary, not the source of
truth.

| Controller | Route | What it's for |
|---|---|---|
| `TradeController` | `api/trade` | Query trades — filter by asset (in/out/pair), txId, trader, pool, protocol, trade state, block/timestamp range; paginated |
| `LiquidityController` | `api/liquidity` | Query liquidity add/remove events — asset pair, txId, pool address/appId; paginated |
| `PoolController` | `api/pool` | List/filter pools by protocol, asset pair, or address, with ordering; `api/pool/stats` |
| `AggregatedPoolController` | `api/aggregated-pool` | Aggregated view across pools for an asset pair — TVL, 1h/24h/7d volume, pool count, with a "light" mode that omits nested pool detail |
| `AssetController` | `api/asset` | Asset metadata lookup by id/search, plus `GET api/asset/image/{assetId}` icon retrieval — **public, no auth**, so the charting widget can resolve tickers directly |
| `AssetStatController` | `api/asset-stat` | Backend-computed per-asset TVL/volume/fees/APR rollups (see [`.github/copilot-instructions.md`](.github/copilot-instructions.md#asset-stats-backend-computed-tvlvolumefeesapr) for the APR formula), per-protocol or combined |
| `AssetTimeseriesController` | `api/asset/timeseries` | 7-day hourly USD-price and TVL OHLC series for up to 100 assets at once, for sparkline/column UI, served from an hourly-refreshed cache |
| `TopAssetsController` | `api/asset/top` | Homepage highlight lists — Popular, Trending, Top/Bottom gainers, Top liquidity movers — recomputed every 5 minutes |
| `OHLCController` | `api/OHLC` | TradingView UDF-compatible datafeed (config, symbol resolution/search, history, marks, quotes) — **public, no auth**, embedded directly by the charting widget |
| `SearchController` | `api/search` | Free-text search across assets and pools (`?q=`) |
| `IndexerController` | `api/indexer` | Current indexer status (round/progress) |
| `StatsController` | `api/Stats` | `GET api/stats/dex` — DefiLlama DEX-adapter-style aggregated 24h volume/fees per protocol — **public, no auth** |
| `GossipController` | `api/Gossip` | Gossip relay connectivity health (connected relays, messages delivered, last-delivered time) — **public, no auth** |
| `SignalRTestController` | `api/signalr` | Diagnostics only — verifying ARC-14 auth and the hub broadcast pipeline. Not for production client use |

Every other controller requires `[Authorize]` (ARC-14, see below). Before
adding `[Authorize]` to anything under `api/asset` or `api/OHLC`, check
`../biatec-charting-widget/src/*.ts` for calls to that path first — those
routes are relied on unauthenticated by design.

## SignalR

Hub is mapped at **`/biatecScanHub`** ([`AVMTradeReporter/Hubs/BiatecScanHub.cs`](AVMTradeReporter/Hubs/BiatecScanHub.cs)).
The fullest write-up, including a JS client snippet and filter syntax
examples, is the Swagger description itself —
[`AVMTradeReporter/doc/description.md`](AVMTradeReporter/doc/description.md),
"SignalR hub (/biatecScanHub)" section.

**Client-invocable methods:**

| Method | Auth | Purpose |
|---|---|---|
| `TestConnection()` | anonymous | Debug: echoes the caller's auth/claims context |
| `SubscribeToOHLC(assetIdA, assetIdB)` / `UnsubscribeFromOHLC(...)` | anonymous | Join/leave a canonicalized `ohlc-{min}-{max}` group for live OHLC ticks — used directly by the charting widget |
| `Subscribe(SubscriptionFilter filter)` | `[Authorize]` | Stores a per-connection filter and immediately replays matching buffered recent data |
| `Unsubscribe()` | `[Authorize]` | Clears the connection's filter |

`SubscriptionFilter` fields (protocol, trader, pool address/id, asset id,
aggregated-pool pair id, min trade value USD, trade state, plus
`RecentTrades`/`RecentLiquidity`/`RecentPool`/`RecentAssets`/
`RecentAggregatedPool`/`RecentAssetStats`/`RecentBlocks`/
`MainAggregatedPools` boolean replay toggles) select which of the events
below a connection receives, and how much recent history it gets replayed
from a bounded (last-200) in-memory queue on subscribe.

**Server-pushed event names** (`BiatecScanHub.Subscriptions`): `Trade`,
`Liquidity`, `Block`, `Pool`, `AggregatedPool`, `Asset`, `AssetStat`, `OHLC`,
plus `Error`/`Info`.

Query-string tokens (`?access_token=...`) are supported for browser
EventSource-style clients — `Program.cs` moves `access_token` into the
`Authorization` header for any request under `/biatecScanHub` before auth
runs.

## Authentication

The API uses **[ARC-0014](https://arc.algorand.foundation/ARCs/arc-0014)**
("Algorand transaction signature as authentication") via the
[`AlgorandAuthentication`](https://github.com/scholtz/AlgorandAuthenticationDotNet)
NuGet package. Instead of a password or long-lived API key, the client signs
a zero-fee, unbroadcast Algorand transaction whose `Note` field encodes a
realm (`BiatecScan#ARC14` here) — the transaction's own validity window
(`firstValid`/`lastValid`) doubles as the token's expiry, so there's no
separate refresh step. The base64-encoded signed transaction is sent as
`Authorization: SigTx <...>`; the server verifies the signature against
Algod without ever broadcasting it. Swagger exposes this as an `arc14`
security scheme — recommended client libraries are `arc14`/`arc76`/`algosdk`
(JS/TS) or the `Algorand4` .NET SDK.

**Rate limits**: 60 requests/minute for unauthenticated callers (partitioned
by client IP), 300 requests/minute for callers presenting a valid ARC-14
token (partitioned by their verified Algorand address). Exceeding the limit
returns `429` with a `Retry-After` header. `/health` is exempt.

**Health check**: `GET /health` returns a standard
Healthy/Degraded/Unhealthy JSON report (Elasticsearch, Redis, asset-cache,
and pool-cache checks) — 200 for Healthy/Degraded, 503 for Unhealthy. Used by
the Kubernetes startup/readiness/liveness probes and the external uptime
monitor; excluded from both rate limiting and access logs.

## Building and running locally

```bash
dotnet restore                       # ~20s
dotnet build                         # ~12s
cd AVMTradeReporter && dotnet run    # starts on http://localhost:5135
```

Then open `http://localhost:5135/swagger`. The app starts without
Elasticsearch/Algod/Redis configured (it logs connection errors but keeps
running) — copy `AVMTradeReporter/appsettings.example.json` to
`appsettings.Development.json` as a starting point, and set
`AppConfiguration:Redis:Enabled` to `false` if you don't have Redis running
locally. A **CORS configuration is mandatory** — the app fails to start
without at least one entry under `Cors` in config.

## Testing

```bash
dotnet test    # ~7s
```

A large share of tests require a live Algod/network connection and are
expected to fail in an isolated environment — this is normal, not a
regression. Tests under `Model/`, `Repository/` (offline-compatible slices),
and pure calculation tests (e.g. `ClAMMTest`, `GetIntervalBuckets_*`,
`FromPools_*`) should pass without network access.

## Other docs in this repo

| Doc | Covers |
|---|---|
| [`docs/STAGE_ENVIRONMENT.md`](docs/STAGE_ENVIRONMENT.md) | Stage (testnet) vs. production isolation — namespaces, ConfigMaps, Secrets, Redis prefixes, hostnames |
| [`docs/CICD_GITHUB_ACTIONS.md`](docs/CICD_GITHUB_ACTIONS.md) | `deploy.yml` (auto stage deploy) vs. `promote-production.yml` (manual prod/voi promotion), required Secrets/Environments |
| [`docs/ICON_SHARING.md`](docs/ICON_SHARING.md) | How `GET /api/asset/image/{assetId}` resolves ASA icons and why stage/production share the same on-disk cache |
| [`AVMTradeReporter/POOL_CACHE_README.md`](AVMTradeReporter/POOL_CACHE_README.md) | `PoolRepository`'s in-memory cache + Redis persistence design |
| [`AVMTradeReporter/POOL_AUTO_ENRICHMENT.md`](AVMTradeReporter/POOL_AUTO_ENRICHMENT.md) | Auto-filling pool metadata that trade/liquidity-derived pool records don't carry |
| [`AVMTradeReporter/POOL_REFRESH_CONFIGURATION.md`](AVMTradeReporter/POOL_REFRESH_CONFIGURATION.md) | Configuring the periodic full pool-refresh background service |
| [`ASYNC_BLOCK_PROCESSING.md`](ASYNC_BLOCK_PROCESSING.md) | Asynchronous block processing so indexing doesn't fall behind Algorand's ~3.3s block time |
| [`REDIS_PUBSUB_IMPLEMENTATION.md`](REDIS_PUBSUB_IMPLEMENTATION.md) | Redis PubSub events for pool/aggregated-pool updates |
| [`NUGET_PUBLISHING.md`](NUGET_PUBLISHING.md) | Automatic NuGet publishing of `AVMTradeReporter.Models` |
| [`IMPLEMENTATION_SUMMARY.md`](IMPLEMENTATION_SUMMARY.md) | Historical implementation checklist (Models NuGet extraction + Redis pubsub) |
| [`.github/copilot-instructions.md`](.github/copilot-instructions.md) | Agent/Copilot working guide — build/test commands, project structure, USD valuation and asset-stat architecture notes |
| [`CLAUDE.md`](CLAUDE.md) | Claude Code project instructions — deploy conventions, rollout safety rules, cross-repo workflow with the frontend |
| [`AVMTradeReporter/doc/description.md`](AVMTradeReporter/doc/description.md) | The full Swagger top-level description — architecture, data flow, and the fullest SignalR usage write-up |

## Related repositories

- **[`biatec-scan-web`](https://github.com/scholtz/biatec-scan-web)** — the
  Vue 3 frontend that consumes this API and its SignalR hub. Mints ARC-14
  tokens client-side (`arc14`/`arc76`/`algosdk`) and embeds the charting
  widget as an iframe.
- **`biatec-charting-widget`** — an unauthenticated TradingView chart client
  embedded by `biatec-scan-web`, calling only this API's public endpoints
  (`api/asset`, `api/OHLC/*`, `api/asset/image/{assetId}`).
- **`BiatecCLAMM`** — the on-chain CLAMM contracts for the Biatec DEX
  protocol this service indexes trades/liquidity/pools for, alongside Pact
  and TinyMan.
