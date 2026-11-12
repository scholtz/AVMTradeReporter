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
once in the `build` job and reused by the stage deploy job, for both `scholtz2/avm-trade-reporter` and
`scholtz2/avm-trade-reporter-subscriber`.

Two jobs run on every push to `master`:

1. **build** - builds and pushes both Docker images to Docker Hub.
2. **deploy-stage** (needs `build`) - deploys the exact same images to a stage environment that runs
   against Algorand **testnet** instead of mainnet. See [`STAGE_ENVIRONMENT.md`](./STAGE_ENVIRONMENT.md)
   for what stage is for and how it's isolated from production. Unlike production, stage's Elasticsearch
   and Redis credentials are not managed by hand in the cluster - they come entirely from GitHub secrets
   and are written into the `avm-trade-reporter-stage-secret` / `avm-trade-reporter-subscriber-stage-secret`
   Kubernetes Secrets fresh on every run.

**Production is no longer deployed automatically from this workflow.** Promoting a version to
production is a separate, manual-only pipeline: `.github/workflows/promote-production.yml`, triggered
by hand (`workflow_dispatch`) with the version string to promote (as produced by the `build` job above,
e.g. `1.2026.08.04-main`). It re-tags that version's already-pushed images as `:latest` on Docker Hub,
then updates `k8s/main/*.yaml` image tags, commits them back to the repo, `kubectl apply`s them against
the `biatec-scan` namespace, and restarts the mainnet Deployments - the same steps the old
`deploy-production` job used to run automatically. This is the direct replacement for the old
`deploy-trade-reporter.sh` + `update-config.sh` flow, just moved behind a manual gate instead of running
on every push.

## Required GitHub Secrets

The workflows use GitHub's **Environments** feature to keep stage-only credentials physically separate
from everything else - the `deploy-stage` job (in `deploy.yml`) runs under the `stage` Environment and
the `deploy-production` job (in `promote-production.yml`) runs under the `production` Environment (each
declared via `environment:` in its workflow file). A secret defined inside an Environment is only ever
visible to jobs running under that Environment; it cannot be read by the `build` job or by the other
deploy job. Repo secrets, by contrast, are visible to every job, so put a value there only when it's
genuinely shared.

### Global (repository) secrets

Set these under **Settings → Secrets and variables → Actions → Secrets** (the repository-level tab,
*not* inside an Environment). Used by every job, so there is exactly one value regardless of
environment.

| Secret | Used by | Purpose |
|---|---|---|
| `DOCKERHUB_USERNAME` | `build` | Docker Hub username to push `scholtz2/avm-trade-reporter*` images. |
| `DOCKERHUB_TOKEN` | `build` | Docker Hub access token (Account Settings → Security → New Access Token) for the user above. |
| `KUBE_CONFIG` | *(not here any more)* | Lives in each deploy Environment (`stage`, `production`, `production-voi`) since 2026-09-12 - an Environment secret of the same name takes priority over a repository one. See "Deployer credentials" below for what it must contain. |

### `stage` Environment secrets

Create the Environment first (**Settings → Environments → New environment → `stage`**), then add these
under its own **Environment secrets** section (not the repo-level Secrets tab). They are used **only**
by the `deploy-stage` job (and the weekly credential check). Besides the ones below, it holds `KUBE_CONFIG` - see
"`KUBE_CONFIG` - one per Environment".

| Secret | Purpose |
|---|---|
| `ELASTIC_HOST` | Elasticsearch host URL for the **stage** environment. **Should point at an Elasticsearch cluster/host separate from production's** - AVMTradeReporter's index names are fixed in code (not parameterized by environment), so stage and production writing to the same Elasticsearch host would mix testnet and mainnet documents in the same indices. |
| `ELASTIC_API_KEY` | API key for the host above. |
| `REDIS_CONNECTION_STRING` | Redis connection string for stage (e.g. `redis-headless.redis:6379`). Safe to reuse production's Redis instance if desired - stage uses distinct key prefixes and pub/sub channels (`avmtrade:stage:*` vs. production's `avmtrade:*`), configured in `k8s/stage/conf-api-stage/appsettings.json` and `k8s/stage/subscriber-deployment-stage.yaml`, so the two environments' keys/channels never collide even on a shared instance. |

### `production` Environment secrets

Create this Environment too (**Settings → Environments → New environment → `production`**). Its only secret is
`KUBE_CONFIG` - the kubeconfig of the `github-deployer-production` ServiceAccount (see "Deployer credentials").
Production's own credentials (`avm-trade-reporter-secret` / `avm-trade-reporter2-secret` /
`avm-trade-reporter-subscriber-secret` in the `biatec-scan` namespace - the mainnet Elastic/Redis/Algod config) are
**not** managed by this workflow and continue to be set by hand directly in the cluster. Attach protection rules
(required reviewers) here: they gate production promotions.

### `KUBE_CONFIG` - one per Environment

Every deploy Environment holds its own `KUBE_CONFIG` - `stage`, `production` and `production-voi` - each the
kubeconfig of that environment's own deployer ServiceAccount, whose Role only reaches that environment's objects (a
repository-level `KUBE_CONFIG` is not used; an Environment secret of the same name would take priority anyway).
`k8s/ci/make-kubeconfig.sh --set-secrets` sets all three. Details and rotation: "Deployer credentials" below.

## Deployer credentials (`KUBE_CONFIG`)

Every deploy job starts with the local action `.github/actions/kube-setup`: it installs kubectl, writes the base64
`KUBE_CONFIG` secret of the job's Environment to `~/.kube/config` and runs `scripts/check-kubeconfig.sh` **before**
anything is applied. The check prints the API server, the kind of credential and its expiry (never the credential
itself), fails with an explanation when the cluster rejects it or it may not deploy, and warns 14 days before it
expires. `.github/workflows/kube-credentials.yml` runs the same check every Monday for `stage` and `production-voi`
and fails 14 days ahead of an expiry (`production` is checked by its promote job instead - it needs a reviewer, a
scheduled job would wait for one every week). Before anything in the cluster changes, a preflight step runs a
server-side dry run of the manifests (production: also of the images PV/PVC) and, for stage and Voi, checks that the
shared images PVC exists. The manifests are committed back to `master` only after a **successful** rollout
(`.github/actions/commit-manifests`): promote-production's gate reads `master`'s stage manifest as "stage runs this
version", and after a failed rollout the old pods keep serving - so `master` keeps showing the version that serves.
The commit is made on a fresh `origin/master` worktree (no rebase that another deploy's commit could conflict with),
fetch / push failures are retried and a push that keeps failing fails the job. Overlapping deploys of one environment
are serialized (`concurrency`): GitHub keeps at most one waiting deploy per environment, so a deploy that was still
waiting when a newer push arrived shows as *cancelled* - the newer one deploys instead.
A credential whose expiry cannot be read (e.g. a Rancher token) makes the weekly check fail until it is replaced.

**History:** the secrets used to hold a copy of a personal/admin kubeconfig whose credential expires about 30 days
after it is issued. The stage deploy broke on 2026-09-09 (fixed 2026-09-12 by pasting fresh kubeconfigs into all
three Environments) and again from 2026-10-18, every run failing at the first `kubectl` call with
`failed to download openapi: the server has asked for the client to provide credentials` - an HTTP 401: the cluster
rejected the expired credential. Because the manifests were committed before that call, `master` collected
`cicd deploy ... to stage` commits for versions that never reached the cluster.

**Fix / rotation:** one ServiceAccount per Environment (`k8s/ci/github-deployer.yaml`): `github-deployer-stage`,
`github-deployer-production`, `github-deployer-voi`. Each token Secret (`kubernetes.io/service-account-token`) does
not expire and is revoked by deleting it. Every Role only reaches its own environment's objects by name, no Role may
create Deployments / Pods or list Secrets, and only the production deployer touches the shared images PV/PVC.

This limits each credential through the API, but it is **not a security boundary** between the environments: they
share the namespace `biatec-scan`, so a deployer that may patch its own Deployment may also change its pod template,
and a pod can mount any Secret of its namespace. Isolating the stage job (every push, no reviewer) from production
needs stage in its own namespace - blocked today by the shared icon cache (a PersistentVolume binds to one PVC in one
namespace) - or an admission policy pinning the pod templates' Secret / ServiceAccount references.
With a cluster-admin kubectl context:

```bash
k8s/ci/make-kubeconfig.sh --set-secrets   # applies the RBAC, builds + verifies the 3 kubeconfigs, then sets the secrets
gh workflow run deploy.yml --ref master --repo scholtz/AVMTradeReporter   # deploys the current master to stage
```

Do not re-run an *older* failed run: it re-deploys that run's (older) images and records their tags on `master`.
Re-running the newest failed run is fine. Nothing is set unless all three kubeconfigs verified.

The kubeconfigs are written to a temporary directory outside the repository and deleted after `--set-secrets`. If
the admin context reaches the cluster through a proxy (a Rancher `/k8s/clusters/...` URL does not accept
ServiceAccount tokens), pass the cluster's own API endpoint: `KUBE_SERVER=https://<host>:6443 k8s/ci/make-kubeconfig.sh --set-secrets`.
To revoke one: `kubectl -n biatec-scan delete secret github-deployer-stage-token` (re-running the script mints a new
token and updates the secret).

**Limits by design:** the deployers only `kubectl apply` objects that already exist (get + patch). A new object - a
new Deployment, Service, Ingress, ConfigMap or Secret name, or the images PV/PVC after a cluster rebuild -
is created once by an admin, and its name added to the matching Role in `k8s/ci/github-deployer.yaml`. Until then
the deploy fails with `forbidden`, naming the object.

## One-time cluster prerequisites

- The `biatec-scan` namespace, and production's Secrets (`avm-trade-reporter-secret`,
  `avm-trade-reporter2-secret`, `avm-trade-reporter-subscriber-secret`) must already exist (unchanged
  from before this workflow).
- DNS records for the two new stage hostnames must point at the same ingress-nginx load balancer IP
  as the production hostnames, before the first `deploy-stage` run, so cert-manager can issue TLS
  certificates for them:
  - `stage-algorand-trades.de-4.biatec.io`
  - `testnet.scan.biatec.io`

## Manifests updated by these workflows

- `deploy.yml`: `k8s/stage/deployment-api-stage.yaml`, `k8s/stage/subscriber-deployment-stage.yaml`
  (stage image tags), on every push to `master`.
- `promote-production.yml`: `k8s/main/deployment-api.yaml`, `k8s/main/deployment2-api.yaml`,
  `k8s/main/subscriber-deployment.yaml` (production image tags), only when manually triggered.

Each deploy job first applies the manifests with `kubectl apply` and watches the rollout, then - only when the rollout
succeeded - records the deployed image tags on `master` with `[skip ci]` (only the tags are re-applied on a fresh
`origin/master`, so concurrent edits to the same files are kept). The checked-in YAML therefore names the version that
actually serves; a failed run records nothing.
