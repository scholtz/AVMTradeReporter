# CI/CD via GitHub Actions

`.github/workflows/deploy.yml` builds and deploys AVMTradeReporter on every push to `master`. It
replaces the previous flow, which SSH'd into a staging host and ran `deploy-trade-reporter.sh` there
(that script did `git pull` + `./compose.sh` (`docker build`/`docker push`) + `sed` the image tag into
the checked-in manifests + `./update-config.sh` (`kubectl apply` + rollout restart), all executed
_on the remote host itself_). All of that now happens natively inside the GitHub Actions runner:
Docker images are built and pushed from the runner, and the runner talks to the Kubernetes API
directly via a kubeconfig secret. Nothing SSHes into any host anymore, and `deploy-trade-reporter.sh`
is no longer used (safe to delete from the old staging host once this workflow is verified).

The image tag naming is unchanged: `1.<year>.<month>.<day>-main` (e.g. `1.2026.08.04-main`), computed
once in the `build` job and reused by both the production and stage deploy jobs, for both
`scholtz2/avm-trade-reporter` and `scholtz2/avm-trade-reporter-subscriber`.

Three jobs run on every push to `master`:

1. **build** - builds and pushes both Docker images to Docker Hub.
2. **deploy-production** (needs `build`) - updates `k8s/main/*.yaml` image tags, commits them back to
   the repo, then `kubectl apply`s them against the `biatec-scan` namespace and restarts the mainnet
   Deployments. This is the direct replacement for the old `deploy-trade-reporter.sh` + `update-config.sh`
   flow.
3. **deploy-stage** (needs `build`) - the new part: deploys the exact same images to a stage
   environment that runs against Algorand **testnet** instead of mainnet. See
   [`STAGE_ENVIRONMENT.md`](./STAGE_ENVIRONMENT.md) for what stage is for and how it's isolated from
   production. Unlike production, stage's Elasticsearch and Redis credentials are not managed by hand
   in the cluster - they come entirely from GitHub secrets and are written into the
   `avm-trade-reporter-stage-secret` / `avm-trade-reporter-subscriber-stage-secret` Kubernetes Secrets
   fresh on every run.

Both deploy jobs run in parallel and are independent - a failure in one does not block the other.

## Required GitHub Secrets

The workflow uses GitHub's **Environments** feature to keep stage-only credentials physically separate
from everything else - the `deploy-stage` job runs under the `stage` Environment and the
`deploy-production` job runs under the `production` Environment (each declared via `environment:` in
`.github/workflows/deploy.yml`). A secret defined inside an Environment is only ever visible to jobs
running under that Environment; it cannot be read by the `build` job or by the other deploy job. Repo
secrets, by contrast, are visible to every job, so put a value there only when it's genuinely shared.

### Global (repository) secrets

Set these under **Settings → Secrets and variables → Actions → Secrets** (the repository-level tab,
*not* inside an Environment). Used by every job, so there is exactly one value regardless of
environment.

| Secret | Used by | Purpose |
|---|---|---|
| `DOCKERHUB_USERNAME` | `build` | Docker Hub username to push `scholtz2/avm-trade-reporter*` images. |
| `DOCKERHUB_TOKEN` | `build` | Docker Hub access token (Account Settings → Security → New Access Token) for the user above. |
| `KUBE_CONFIG` | `deploy-production`, `deploy-stage` | **Base64-encoded** kubeconfig with write access to the `biatec-scan` namespace (Deployments, Services, Ingresses, ConfigMaps, and - for stage only - Secrets). Generate with `kubectl config view --minify --flatten \| base64 -w0` from a context scoped to this namespace; a dedicated ServiceAccount + RBAC Role is recommended over reusing a personal/admin kubeconfig. Kept global because today one kubeconfig has access to both environments' resources in the same namespace/cluster - see "Splitting `KUBE_CONFIG` per environment" below if you want to change that. |

### `stage` Environment secrets

Create the Environment first (**Settings → Environments → New environment → `stage`**), then add these
under its own **Environment secrets** section (not the repo-level Secrets tab). They are used **only**
by the `deploy-stage` job.

| Secret | Purpose |
|---|---|
| `ELASTIC_HOST` | Elasticsearch host URL for the **stage** environment. **Should point at an Elasticsearch cluster/host separate from production's** - AVMTradeReporter's index names are fixed in code (not parameterized by environment), so stage and production writing to the same Elasticsearch host would mix testnet and mainnet documents in the same indices. |
| `ELASTIC_API_KEY` | API key for the host above. |
| `REDIS_CONNECTION_STRING` | Redis connection string for stage (e.g. `redis-headless.redis:6379`). Safe to reuse production's Redis instance if desired - stage uses distinct key prefixes and pub/sub channels (`avmtrade:stage:*` vs. production's `avmtrade:*`), configured in `k8s/stage/conf-api-stage/appsettings.json` and `k8s/stage/subscriber-deployment-stage.yaml`, so the two environments' keys/channels never collide even on a shared instance. |

### `production` Environment secrets

Create this Environment too (**Settings → Environments → New environment → `production`**) - it holds
**no secrets today**. Production's own credentials (`avm-trade-reporter-secret` /
`avm-trade-reporter2-secret` / `avm-trade-reporter-subscriber-secret` in the `biatec-scan` namespace -
the mainnet Elastic/Redis/Algod config) are **not** managed by this workflow at all and continue to be
set by hand directly in the cluster, same as before this change. The Environment still needs to exist
so the `deploy-production` job's `environment: production` reference resolves and (optionally) so you
can attach protection rules (e.g. required reviewers) to production deploys later.

### Splitting `KUBE_CONFIG` per environment (optional hardening)

If you'd rather the stage job's kubeconfig not be able to touch production resources (or vice versa),
add a `KUBE_CONFIG` secret inside the `stage` and/or `production` Environment itself - an
Environment-scoped secret of the same name silently takes priority over the repo-level one for jobs
running under that Environment, so no workflow changes are needed to adopt this later.

## One-time cluster prerequisites

- The `biatec-scan` namespace, and production's Secrets (`avm-trade-reporter-secret`,
  `avm-trade-reporter2-secret`, `avm-trade-reporter-subscriber-secret`) must already exist (unchanged
  from before this workflow).
- DNS records for the two new stage hostnames must point at the same ingress-nginx load balancer IP
  as the production hostnames, before the first `deploy-stage` run, so cert-manager can issue TLS
  certificates for them:
  - `stage-algorand-trades.de-4.biatec.io`
  - `testnet.scan.biatec.io`

## Manifests updated by this workflow

- `k8s/main/deployment-api.yaml`, `k8s/main/deployment2-api.yaml`, `k8s/main/subscriber-deployment.yaml`
  (production image tags)
- `k8s/stage/deployment-api-stage.yaml`, `k8s/stage/subscriber-deployment-stage.yaml` (stage image
  tags)

Each deploy job commits its own manifest changes back to `master` with `[skip ci]` so the checked-in
YAML always reflects what's actually deployed, then applies them with `kubectl apply`.
