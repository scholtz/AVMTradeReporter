# ASA icon image sharing across environments

`GET /api/asset/image/{assetId}` (`AssetController.GetAssetImage`, backed by
`MainnetImageProcessor`) serves a PNG icon for an ASA. This doc covers how that icon is resolved
and why stage (testnet) and production (mainnet) intentionally share the on-disk cache.

## Storage

Icons are cached on disk under `AppContext.BaseDirectory/images/`, on a volume backed by
`avm-trade-reporter-images-pvc` (`k8s/main/pv-images.yaml`) - a ReadWriteMany PersistentVolume
mounted at `/app/images` by every API deployment that serves this endpoint: production's
`avm-trade-reporter-app-deployment` and `avm-trade-reporter2-app-deployment`
(`k8s/main/deployment-api.yaml`, `deployment2-api.yaml`) and stage's
`avm-trade-reporter-stage-app-deployment` (`k8s/stage/deployment-api-stage.yaml`). All three pods
read and write the same files. There is no database or blob storage involved.

Two subdirectories:

- `images/{network}/{assetId}.png` - per-network, id-keyed cache. Kept separate per network
  (`mainnet-v1.0`, `testnet-v1.0`, ...; see `MainnetImageProcessor.NetworkFolder`) because
  Algorand mainnet and testnet asset ids are independent numbering spaces and can collide.
- `images/by-unitname/{unitname}.png` - shared across every network, keyed by the ASA's
  lower-cased `UnitName` (ticker). This is what makes cross-network reuse possible.

## Resolution order (`MainnetImageProcessor.LoadImageAsync`)

1. Check this deployment's own `images/{network}/{assetId}.png`. Return it if present.
2. **Non-mainnet deployments only.** Look up the requested asset's `UnitName` via
   `IAssetRepository` (each deployment only knows about assets on its own network). If found,
   check the shared `images/by-unitname/{unitname}.png`. If present (and not the placeholder),
   serve it and also write it into this deployment's own id-keyed cache for a fast path next time.
   **Mainnet deployments skip this step entirely and never read `by-unitname`** - see "Why mainnet
   never reads the shared cache" below.
3. **Mainnet deployments only** - Tinyman's ASA list and Pera's asset API only index mainnet, so
   non-mainnet deployments skip straight past this step. Try Tinyman, then Pera. Whichever
   succeeds is written to this deployment's id-keyed cache, and *also*, first-writer-wins, into
   `images/by-unitname/{unitname}.png` if no entry exists there yet - so any non-mainnet
   deployment can reuse it via step 2 without ever calling Tinyman/Pera itself.
4. Fall back to a 1x1 transparent placeholder PNG, cached only under the id-keyed path (never
   written to `by-unitname`, so it doesn't block a future real resolution for that ticker).

## Why mainnet never reads the shared cache

Distinct real mainnet assets can share a `UnitName`. This actually happened: "Meld Gold" and
"ASA.Gold" are two unrelated projects that both use a `GOLD`-style ticker, and the first version
of this feature let mainnet consult `by-unitname` for its own lookups - so whichever of the two
resolved its icon first via Tinyman/Pera got cached under the shared ticker key, and the *other*
project's requests then hit that same cached file and silently served the wrong project's icon.

Mainnet identity must stay strictly asset-id-keyed: step 2 above is gated on `!IsMainnet`, so a
mainnet deployment only ever *writes* to `by-unitname` (to help other networks), never *reads*
from it. Ticker collisions are an accepted, documented heuristic risk for the *non-mainnet*
fallback only (see "Caveats" below) - never for mainnet, where every asset must resolve to its own
distinct icon regardless of what other assets share its ticker.

## Why UnitName, not asset id or full Name

The user asked for "same name" reuse. UnitName (the ASA ticker, e.g. `USDC`) was chosen over the
full asset `Name` field because tickers are short, standardized, and much less likely to collide
by coincidence than free-text names - a testnet faucet token is more likely to reuse the exact
ticker `USDC` than the exact display name text. Matching is case-insensitive exact-match on the
normalized (lowercased, alphanumeric/`-`/`_` only) UnitName.

## Caveats

- This is a heuristic, not a verified mapping - a testnet asset with `UnitName = "USDC"` gets the
  real USDC icon even if it isn't actually a Circle-issued or otherwise "official" test token.
  Acceptable for a low-stakes icon, not for anything security-relevant.
- The shared PVC is backed by a `hostPath` volume
  (`/mnt/nvme1/biatec-scan-shared-drive/avm-trade-reporter-images` on the underlying node) - see
  the caveats in `k8s/main/pv-images.yaml` about hostPath only providing real sharing when every
  consuming pod lands on that same node.

## Staleness refresh (self-healing)

An id-keyed cache file older than `MainnetImageProcessor.RefreshInterval` (7 days) is re-resolved
from source (step 2/3 above) the next time it's requested, instead of being served forever as-is.
This exists so a bad icon - like the Meld Gold / ASA.Gold mixup above, written before the mainnet
`by-unitname` read was disabled - eventually self-heals without anyone having to manually delete
the file from the shared volume, and so an upstream project that fixes its Tinyman/Pera icon
eventually propagates here too.

The refresh is fail-safe in both directions:

- A fresh result only replaces the cached file if it passes `IsUsableImage` (at least
  `MinUsableImageBytes` = 256 bytes and not the 1x1 placeholder) - a failed HTTP call, a 404, or a
  truncated/corrupt download never overwrites a working cached icon.
- If the refresh finds nothing usable, the stale-but-valid cached icon keeps being served, and the
  next attempt happens on the following request past the TTL - not immediately, and not by falling
  back to the placeholder.
