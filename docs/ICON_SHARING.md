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
2. Look up the requested asset's `UnitName` via `IAssetRepository` (each deployment only knows
   about assets on its own network). If found, check the shared
   `images/by-unitname/{unitname}.png`. If present (and not the placeholder), serve it and also
   write it into this deployment's own id-keyed cache for a fast path next time.
3. **Mainnet deployments only** - Tinyman's ASA list and Pera's asset API only index mainnet, so
   non-mainnet deployments skip straight past this step. Try Tinyman, then Pera. Whichever
   succeeds is written to both the id-keyed cache *and* the by-unitname cache, so any other
   network's deployment can reuse it via step 2 without ever calling Tinyman/Pera itself.
4. Fall back to a 1x1 transparent placeholder PNG, cached only under the id-keyed path (never
   written to `by-unitname`, so it doesn't block a future real resolution for that ticker).

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
