# Project notes for Claude

## Adding [Authorize] to a controller: check the charting widget's dependency first

`../biatec-charting-widget` is a *separate*, unauthenticated browser client
(embedded as an iframe via `/charts` — see this frontend repo's
`assetChartUrl()`/`chartsBaseUrl` in `../biatec-scan-web/src/config/env.ts`)
with no ARC-14 signing capability: it can only call endpoints that stay
`[AllowAnonymous]`. It depends on `GET /api/asset` (asset id → ticker/symbol
resolution, `getAllSymbols()` in `biatec-charting-widget/src/datafeed.ts`)
in addition to the already-obviously-public `GET /api/OHLC/*` datafeed
endpoints and `GET /api/asset/image/{assetId}`.

2026-08-13 incident: the 2026-08-12 "secure remaining public endpoints"
commit added a controller-level `[Authorize]` to `AssetController` without
noticing the charting widget's dependency on `GET /api/asset`. Every mainnet
chart silently fell back to its hardcoded default symbol ("ALGORANDUSD")
with all-zero OHLC values, because `getAllSymbols()`'s anonymous fetch
started failing and `main.ts` swallows that error. Fixed by adding
`[AllowAnonymous]` back to `AssetController.GetAssets` specifically (not the
whole controller — future new actions there should default to authenticated
unless they have the same public/read-only justification). Regression test:
`AVMTradeReporterTests/Controllers/AssetControllerAuthorizationTests.cs`
(reflection-based — asserts the `[AllowAnonymous]` attribute is present,
rather than spinning up a full authenticated host).

**Rule going forward**: before adding `[Authorize]` to any controller/action
under `api/asset`, `api/OHLC`, or anything else the charting widget might
touch, grep `biatec-charting-widget/src/*.ts` for calls to that path first,
and update `doc/description.md`'s public-endpoint list either way.

## HA deploys: a new pod must be fully warm before it ever gets traffic

`Program.cs` blocks Kestrel's port bind on synchronously (`.Wait()`-ed)
loading every static in-memory cache: `AssetRepository.EnsureInitializedAsync`,
`PoolRepository.InitializeAsync` (which internally also drives
`AggregatedPoolRepository.InitializeFromExistingPoolsAsync`), and
`TradeReporterBackgroundService.StartAsync`. This is deliberate: the
readiness/liveness probes below are a bare `tcpSocket: {port: 8080}` check,
so "the port is open" is the *only* signal Kubernetes gets that this pod is
safe to receive traffic — there is no separate `/health/ready` HTTP endpoint.
That means **every** piece of state the API serves (asset prices/TVL/pool
counts/volumes, pool listings, aggregated pool pairs) must be fully computed
*before* `app.Run()` is reached, or a freshly-rolled pod will pass its
readiness check and start serving incomplete/zero data while the old pod
(which had it right) is already gone.

This exact bug happened once already (2026-08-13): `AggregatedPoolRepository
.InitializeFromExistingPoolsAsync` recomputed each asset's derived
PriceUSD/TVL_USD/PoolsCount/Volume figures from the pool cache, but did the
work in a fire-and-forget `_ = Task.Run(...)` instead of awaiting it. The
outer `poolRepository.InitializeAsync(...).Wait()` in `Program.cs` returned
(and Kestrel opened the port) long before that background task finished, so
a newly-deployed pod could look "ready", get real user traffic, and serve
stale/incomplete Assets-page numbers for however long the O(pairs²) recompute
took to catch up — with the previous, fully-warm pod already terminated.
Fixed by awaiting it inline as part of the blocking startup chain.

**Rule going forward**: any new repository with a static in-memory cache
(the `AssetRepository` / `PoolRepository` / `AggregatedPoolRepository`
pattern) must have its full warmup — including any derived/computed fields,
not just the raw cache entries — awaited synchronously from `Program.cs`
before `app.Run()`. Never fork startup-critical population work into a
fire-and-forget `Task.Run`; if you need it off the request path, that's what
`IHostedService`/`BackgroundService` is for *after* the pod is already
serving traffic on data that's already correct, not for the initial load.
Same rule applies to any future readiness signal: as long as the k8s probes
stay TCP-only, "port open" must remain synonymous with "fully warm",
because there's no finer-grained HTTP health check to fall back on.

**Follow-up incident (2026-08-13, same day, commit `aaa7750` → broke the very
next Algorand mainnet promote): making that warmup awaited instead of
fire-and-forget turned a *correctness* bug into an *availability* one.**
`AggregatedPoolRepository.InitializeFromExistingPoolsAsync` called
`UpdateRelatedAssetsAsync` once per pool, and that method's last step
(`RefreshPoolHistoricalPricesAsync` today) rescanned the *entire* aggregated-
pool cache to refresh denormalized historical-price fields on every cached
pool — an O(pools) rescan repeated once per pool, i.e. O(pools²). While this
ran fire-and-forget in the background, nobody noticed. Once it became
startup-blocking, production's real pool count made it take longer than both
the `startupProbe` budget and `kubectl rollout status`'s default 10-minute
progress deadline — the promote workflow's "Roll out production deployments"
step failed with `deployment ... exceeded its progress deadline`, stuck at
"N old replicas are pending termination" the whole time (translation: the new
pod never became healthy; `maxUnavailable: 0` meant the old pod was never
touched, so this was a failed *deploy*, not an outage — but every retry would
fail identically until fixed). Voi's smaller pool count deployed fine in the
same run, which is why this didn't show up as a generic "the probes are
broken" symptom.

Fixed by splitting the per-pool-call path from the bulk-init path:
`RecomputeAssetStatsAsync` now takes an explicit list of asset ids and is
called once with the *small, fixed-size* "affected" set (updated pool's two
assets + ALGO + usdRef) from the live per-trade path
(`UpdateRelatedAssetsAsync`), or once with *every distinct asset in the whole
cache, deduplicated* from the bulk path (`RefreshAllAssetStatsAsync`, called
once total by `InitializeFromExistingPoolsAsync`, not once per pool). Same
for the historical-price cache rescan. Net effect: bulk startup is
O(pools + distinct assets), not O(pools²). Regression test:
`AVMTradeReporterTests/Repository/AggregatedPoolBulkInitPerformanceTests.cs`
(asserts `GetAssetAsync` call count during bulk init stays linear in pool
count using a counting `IAssetRepository`, via the new
`AggregatedPoolRepository.ResetForTests()` test-only cache-clear hook).

**Rule going forward**: any code path that runs once per pool/asset/trade
during *live* operation and is safe there (small, fixed fan-out) must not be
reused unmodified for *bulk* operations (startup, full resync) without first
checking whether it internally scans the whole cache/collection - that scan
is O(1) amortized over a stream of independent live events, but O(N) if
repeated once per item during a bulk load of N items. When adding a new bulk
path, prefer a dedicated method that does each expensive collection-wide scan
exactly once, the way `RefreshAllAssetStatsAsync`/`RefreshPoolHistoricalPricesAsync`
do.

**Second follow-up (2026-08-14): O(N) still wasn't fast enough on its own.**
Production has 3000+ pools. Even after fixing the O(pools²) bug above, a
*sequential* O(pools) loop - one ES write + two Redis calls + a hub-
subscription scan per pool, then later one asset recompute (1-3 ES OHLC
queries) per distinct asset - still added up to roughly ten minutes of pure
network round-trip latency (confirmed: production logs went silent for ~10
minutes right after "Updated volumes for 3394 pools", then the rollout timed
out again). Fixed by running both the per-pool store/publish loop
(`InitializeFromExistingPoolsAsync`) and the per-asset recompute loop
(`RecomputeAssetStatsAsync`'s non-priority tier, i.e. everything except
ALGO/usdRef) with `Parallel.ForEachAsync` at a bounded degree of parallelism
(`BulkInitDegreeOfParallelism = 32`) instead of a sequential `foreach`+`await`.
ALGO and usdRef are still resolved *sequentially first* (every other asset's
price can derive from theirs) before the rest fan out. This required
switching the shared `priceCache` from `Dictionary` to `ConcurrentDictionary`
since multiple asset recomputations now run concurrently.

Also bumped, on the same reasoning as above (production's real data volume,
not a bug in the probe config itself): `startupProbe.failureThreshold`
90→180 (~15 min budget) and added an explicit `progressDeadlineSeconds: 1200`
(20 min) on every API Deployment - `kubectl rollout status` (no `--timeout`
flag in the workflows) fails as soon as the Deployment's own
`progressDeadlineSeconds` condition fires, which defaults to 600s and was
the literal error message in both timeout incidents. If a future change
increases startup cost again (more pools, slower ES), raise these three
numbers together - `progressDeadlineSeconds` must always stay comfortably
above `startupProbe`'s own budget, which must stay above whatever the
bulk-warmup path actually takes end to end.

If bulk startup ever needs to be faster again: `BulkInitDegreeOfParallelism`
is the first knob to turn (raise it, watch ES/Redis load), before reaching
for a bigger algorithmic change.

## Kubernetes rollout config (all `k8s/*/deployment-api*.yaml`)

Every API Deployment (`k8s/main/deployment-api.yaml`,
`k8s/main/deployment2-api.yaml`, `k8s/stage/deployment-api-stage.yaml`,
`k8s/voi/deployment-api-voi.yaml`) runs `replicas: 1` and must keep this
shape, fixed 2026-08-13 after a report that mainnet deploys were visibly
dropping/serving-stale data:

- `strategy.rollingUpdate: {maxUnavailable: 0, maxSurge: 1}` +
  `minReadySeconds: 15` — a deploy surges a second pod running the new
  image and only kills the old one once the new one has been continuously
  Ready for 15s. Never remove this or let it default (the bare Deployment
  default is the same numbers, but leave it explicit — it's load-bearing
  for "nobody notices a deploy").
- `startupProbe` (tcpSocket, generous `failureThreshold` ~180 at 5s
  intervals ≈ 15 minutes) must exist on every API container, and the
  Deployment needs an explicit `progressDeadlineSeconds` (1200 = 20 min)
  comfortably above that budget — `kubectl rollout status` in the deploy
  workflows has no `--timeout` of its own and just relays this condition.
  `readinessProbe`/`livenessProbe` share the pod's only real signal (the
  TCP port), so without a `startupProbe` a slow-but-legitimate warmup
  (many pools, cold ES) can get killed mid-startup by the liveness probe's
  short `failureThreshold`, producing an endless restart loop that never
  finishes preloading. A `startupProbe` suppresses liveness/readiness
  until it passes once, however long that takes.
- `livenessProbe.terminationGracePeriodSeconds` must stay reasonable
  (30s, not the old `1s`) — a liveness failure should give in-flight
  requests/SignalR connections a real chance to drain, not an instant
  SIGKILL.

If you add a new production/stage/voi API deployment manifest, copy this
probe/strategy shape rather than the historical bare
`readinessProbe.tcpSocket` + `initialDelaySeconds` pattern.

## Backend counterpart repo

`../biatec-scan-web` is the Vue 3 frontend that consumes this API. See
its `CLAUDE.md` for the cross-repo workflow (backend change → stage deploy →
regenerate typed client → frontend change).
