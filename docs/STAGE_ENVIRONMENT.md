# Stage Environment

Stage is a second, always-on deployment of AVMTradeReporter that indexes Algorand **testnet** instead
of mainnet. It exists so new code can be exercised against real (but low-stakes) chain data before/while
it's also running in production - both environments get the exact same image on every push to `master`
(see `docs/CICD_GITHUB_ACTIONS.md`), so stage is never running older or newer code than production.

## What's isolated between stage and production

| | Production | Stage |
|---|---|---|
| Network (Algod) | Algorand mainnet (`algorand-algod-public.de-4.biatec.io`) | Algorand testnet (`testnet-api.4160.nodely.dev`) |
| Network (gossip relay discovery) | `AppConfiguration.GossipDiscovery.Enabled = true`, `Network = AlgorandMainNet` | `AppConfiguration.GossipDiscovery.Enabled = true`, `Network = AlgorandTestNet` (see note below) |
| Namespace | `biatec-scan` | `biatec-scan` (same namespace, distinct resource names) |
| Deployments | `avm-trade-reporter-app-deployment`, `avm-trade-reporter2-app-deployment`, `avmtradereporter-subscriber` | `avm-trade-reporter-stage-app-deployment`, `avmtradereporter-subscriber-stage` |
| ConfigMap | `avm-trade-reporter-main-conf` | `avm-trade-reporter-stage-main-conf` |
| Secret | `avm-trade-reporter-secret` / `avm-trade-reporter2-secret` / `avm-trade-reporter-subscriber-secret` (managed by hand in-cluster) | `avm-trade-reporter-stage-secret` / `avm-trade-reporter-subscriber-stage-secret` (re-created by CI from the `stage` Environment's `ELASTIC_*`/`REDIS_*` GitHub secrets on every deploy) |
| Redis key prefixes / pub-sub channels | `avmtrade:pools:`, `avmtrade:aggregatedpools:`, `avmtrade:pool:updates`, `avmtrade:aggregatedpool:updates` | `avmtrade:stage:pools:`, `avmtrade:stage:aggregatedpools:`, `avmtrade:stage:pool:updates`, `avmtrade:stage:aggregatedpool:updates` |
| Hostnames | `algorand-trades.de-4.biatec.io`, `api.algorand.scan.biatec.io` | `stage-algorand-trades.de-4.biatec.io`, `testnet.scan.biatec.io` |

Because the two environments share the `biatec-scan` namespace, distinct resource names are what keep
them from colliding - there's no separate stage namespace to fall back on.

> **Gossip relay discovery bug fixed 2026-08-05:** `GossipBackgroundService` discovers gossip relays
> via DNS SRV whenever no static `AppConfiguration.GossipWebsocketClientConfigurations` entry is set
> (true for both environments here). It used to hardcode mainnet, so stage was silently pulling
> mainnet transactions over gossip regardless of `Algod`/`Elastic`/`Redis` being pointed at testnet.
>
> At the time, the `Algorand.Gossip.GossipNetwork` enum (from the `Algorand4` package) only had
> mainnet-family members - `AlgorandMainNet`, `VoiMainNet`, `AramidMainNetBiatec`,
> `AramidMainNetAWallet` - with no testnet value to discover with. `GossipDiscoveryConfiguration.Enabled`
> (new) was added as a stopgap to let a deployment opt out of dynamic discovery entirely when no static
> relay is configured, and stage briefly ran with `Enabled: false` (no gossip-sourced trade feed, still
> indexing every confirmed block via testnet Algod as normal).
>
> **Resolved 2026-08-05** by contributing `GossipNetwork.AlgorandTestNet` upstream
> ([scholtz/dotnet-algorand-sdk#4](https://github.com/scholtz/dotnet-algorand-sdk/pull/4),
> [FrankSzendzielarz/dotnet-algorand-sdk#22](https://github.com/FrankSzendzielarz/dotnet-algorand-sdk/pull/22)),
> mapped to the standard `testnet.algorand.network` SRV domain and genesis ID `testnet-v1.0` - the same
> convention already used for mainnet. Once `Algorand4` `4.7.4.2026080422` (which includes this) was
> published and the `PackageReference` bumped in `AVMTradeReporter.csproj` /
> `AVMTradeReporter.Models.csproj`, stage's ConfigMap was switched to
> `GossipDiscovery: { Enabled: true, Network: "AlgorandTestNet" }`, restoring its gossip-sourced feed.
> Production is unaffected throughout and explicitly sets `Network: AlgorandMainNet` in
> `k8s/main/conf-api/appsettings.json`.

## What is NOT isolated (and why)

- **Elasticsearch indices.** AVMTradeReporter's index names (trades, pools, etc.) are fixed constants
  in code, not parameterized by environment. If `STAGE_ELASTIC_HOST` is pointed at the same
  Elasticsearch cluster as production, testnet documents will land in the same indices as mainnet
  documents. Point `STAGE_ELASTIC_HOST` at a separate cluster/host to avoid this - see
  `docs/CICD_GITHUB_ACTIONS.md`.
- **Redis instance.** `REDIS_CONNECTION_STRING` can safely point at the same Redis instance as
  production, because stage uses entirely distinct key prefixes and pub/sub channel names (see table
  above) - the two environments' keys/messages never overlap even on a shared instance.

## Rotating stage's Elastic/Redis credentials

`ELASTIC_HOST` / `ELASTIC_API_KEY` / `REDIS_CONNECTION_STRING` live inside the `stage` GitHub
Environment (Settings → Environments → stage → Environment secrets) - see
`docs/CICD_GITHUB_ACTIONS.md` for how that's scoped. Update them there, then either push to `master`
or manually re-run the `deploy-stage` job from the Actions tab - the workflow re-creates
`avm-trade-reporter-stage-secret` / `avm-trade-reporter-subscriber-stage-secret` from the current
secret values on every run and rolls out both stage Deployments, so no manual `kubectl` step is
needed.
