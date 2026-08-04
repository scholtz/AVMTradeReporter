# Stage Environment

Stage is a second, always-on deployment of AVMTradeReporter that indexes Algorand **testnet** instead
of mainnet. It exists so new code can be exercised against real (but low-stakes) chain data before/while
it's also running in production - both environments get the exact same image on every push to `master`
(see `docs/CICD_GITHUB_ACTIONS.md`), so stage is never running older or newer code than production.

## What's isolated between stage and production

| | Production | Stage |
|---|---|---|
| Network | Algorand mainnet (`algorand-algod-public.de-4.biatec.io`) | Algorand testnet (`testnet-api.4160.nodely.dev`) |
| Namespace | `biatec-scan` | `biatec-scan` (same namespace, distinct resource names) |
| Deployments | `avm-trade-reporter-app-deployment`, `avm-trade-reporter2-app-deployment`, `avmtradereporter-subscriber` | `avm-trade-reporter-stage-app-deployment`, `avmtradereporter-subscriber-stage` |
| ConfigMap | `avm-trade-reporter-main-conf` | `avm-trade-reporter-stage-main-conf` |
| Secret | `avm-trade-reporter-secret` / `avm-trade-reporter2-secret` / `avm-trade-reporter-subscriber-secret` (managed by hand in-cluster) | `avm-trade-reporter-stage-secret` / `avm-trade-reporter-subscriber-stage-secret` (re-created by CI from `STAGE_ELASTIC_*`/`STAGE_REDIS_*` GitHub secrets on every deploy) |
| Redis key prefixes / pub-sub channels | `avmtrade:pools:`, `avmtrade:aggregatedpools:`, `avmtrade:pool:updates`, `avmtrade:aggregatedpool:updates` | `avmtrade:stage:pools:`, `avmtrade:stage:aggregatedpools:`, `avmtrade:stage:pool:updates`, `avmtrade:stage:aggregatedpool:updates` |
| Hostnames | `algorand-trades.de-4.biatec.io`, `api.algorand.scan.biatec.io` | `stage-algorand-trades.de-4.biatec.io`, `stage-api.algorand.scan.biatec.io` |

Because the two environments share the `biatec-scan` namespace, distinct resource names are what keep
them from colliding - there's no separate stage namespace to fall back on.

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
