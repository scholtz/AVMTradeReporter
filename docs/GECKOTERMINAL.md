# GeckoTerminal (CoinGecko) integration - operations guide

`GET /api/coingecko/{latest-block,asset,pair,events}` implements the *GeckoTerminal Integration API Standards v0.1* for the Biatec
DEX (see `AVMTradeReporter/doc/description.md` for the endpoint contract and `CLAUDE.md` for the design rules). GeckoTerminal
**halts the indexing of a DEX on the first schema violation** and polls every ~2 s, so a bad deploy is invisible to us and fatal
for the listing. This document describes the safety net that keeps a deploy from breaking it.

## The safety net, in the order a change meets it

| Layer | Where | What it proves | Needs |
|---|---|---|---|
| **1. Offline unit + conformance + performance suites** | `ci.yml` on every pull request | the strict schema validator (`Conformance/GeckoTerminalSchema.cs`) accepts everything the real mapper/serializer/ordering code produces, and rejects every kind of violation; mainnet-scale budgets (5 000 pools warm-up < 2 s, 20 000 events/1000 blocks, 1 300 polling cycles, 200 concurrent identical requests = 1 storage query) hold | nothing |
| **2. Live conformance on stage** | `deploy.yml` job `verify-stage`, after every stage rollout | the real stage pods against the real testnet data: schema, no gaps while following the chain, golden historical events unchanged, error contract, latency, 50 concurrent pollers, rate limit | stage deployed |
| **3. Promotion gate** | `promote-production.yml` job `verify-stage` | stage runs **exactly** the version being promoted *and* the live suite passes now; `promote-tag` (`:latest`) and both production deploys wait for it | stage |
| **4. Post-promotion smoke test** | `promote-production.yml` jobs `smoke-production` / `smoke-production-voi` | the same live suite against the rolled-out production API; failure prints the rollback commands | production |
| **5. Scheduled monitor** | `geckoterminal-monitor.yml`, every 6 h | stage and (once `GECKOTERMINAL_MAINNET_LIVE=true`) mainnet keep conforming; a red run is the alert | - |

Run any layer by hand:

```bash
# offline (same as CI)
dotnet test AVMTradeReporterTests --filter "FullyQualifiedName~Services.CoinGecko|FullyQualifiedName~AVMTradeReporterTests.Conformance|FullyQualifiedName~AVMTradeReporterTests.Performance"

# live, against any deployment
GECKOTERMINAL_BASE_URL=https://api.testnet.scan.biatec.io GECKOTERMINAL_GOLDEN=testnet \
  dotnet test AVMTradeReporterTests --filter "Category=Conformance" --logger "console;verbosity=detailed"
```

Environment variables of the live suite: `GECKOTERMINAL_BASE_URL` (required, otherwise every live test is skipped),
`GECKOTERMINAL_GOLDEN` (`testnet` | `mainnet`), `GECKOTERMINAL_EXPECT_EVENTS` (`false` for a network without trades),
`GECKOTERMINAL_POLL_SECONDS` (default 40), `GECKOTERMINAL_SCAN_CHUNKS` (1000-block slices searched for events, default 80),
`GECKOTERMINAL_LATENCY_FACTOR` (multiplier for the latency budgets), `GECKOTERMINAL_WAIT_MINUTES` (how long to wait for a
freshly rolled-out pod).

## What the live suite checks

* **latest-block** conforms, is < 5 min old, advances within 45 s and never moves backwards.
* **events** of a real range (the newest range holding events, or the golden range on a quiet testnet): every field of every
  event against the spec, sorted by `(block, txnIndex, eventIndex)`, `(txnIndex, eventIndex)` unique per block, bounds inclusive,
  one valid swap direction, `priceNative > 0` and equal to the executed price, amounts never more precise than the asset's
  decimals, every `pairId` resolves through `/pair` and every pair asset through `/asset`.
* **splitting** a range in two returns exactly the events of the whole (a poller walking the chain in slices sees everything),
  repeated requests are byte-identical (immutability), a one-block range returns that block's events.
* **error contract**: 400 for bad input, 404 for unknown ids, a retryable **503** (never a partial answer) beyond latest-block.
* **polling simulation** behaves like GeckoTerminal's indexer: every 2 s `latest-block`, then `events(previous+1 .. latest)`,
  validating each answer; no gap, no overlap, no 4xx/5xx.
* **golden data**: recorded historical events and pairs must be reproduced *exactly* - a historical event or a pair's
  asset order / fee changing is precisely what GeckoTerminal never re-reads and would silently keep wrong.
* **latency budgets** (p95): latest-block < 800 ms, cached range < 800 ms, cold 1000-block range < 8 s, asset/pair < 800 ms; 50
  concurrent pollers and 150 back-to-back requests without a 429 (the integration has its own rate-limit bucket).

## Before the first mainnet promotion

1. Merge to `master` -> `deploy.yml` deploys to stage and runs `verify-stage` (must be green).
2. Promote with `promote-production.yml` (the gate re-verifies stage and requires stage to run the promoted version).
3. `smoke-production` runs after the rollout. It uses `Conformance/golden/mainnet.json`; **record it once** from the verified
   production API (the first smoke run without the file only skips the golden test) and commit it:
   `python AVMTradeReporterTests/Conformance/record-golden.py https://api.algorand.scan.biatec.io mainnet`
4. Set the repository variable `GECKOTERMINAL_MAINNET_LIVE=true` so the scheduled monitor watches mainnet too.
5. Tell CoinGecko the base URL: `https://api.algorand.scan.biatec.io/api/coingecko`.

## If the smoke test or the monitor fails on production

Roll back first, investigate second - GeckoTerminal may already be ingesting the bad data:

```bash
kubectl rollout undo deployment/avm-trade-reporter-app-deployment  -n biatec-scan
kubectl rollout undo deployment/avm-trade-reporter2-app-deployment -n biatec-scan
```

Then reproduce on stage with the failing check (the suite output names the exact violation and block range), fix, and go through
the pipeline again. Events GeckoTerminal already stored are never re-requested: a *historical* answer that was wrong has to be
re-indexed by CoinGecko, which is why the golden test and the schema validator are strict about what an immutable range returns.

## Extending the suite

* A new field or rule -> add it to `GeckoTerminalSchema` and a case to `GeckoTerminalSchemaTests` (both directions: the validator
  must reject the broken sample and accept the service's real output).
* A new failure mode seen in production -> reproduce it in `CoinGeckoPerformanceTests` / `CoinGeckoServiceTests` with a budget or
  an assertion, then (if it is only visible live) add a test to `GeckoTerminalConformanceTests`.
* The benchmarks print their measurements (`dotnet test ... --logger "console;verbosity=detailed"`); tighten a budget when the
  numbers leave a lot of headroom, never loosen one to make a regression pass.
