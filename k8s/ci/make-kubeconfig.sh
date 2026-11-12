#!/usr/bin/env bash
# Builds the kubeconfigs of the GitHub Actions deploy jobs from the per-environment deployer ServiceAccounts
# (non-expiring, revocable tokens - see k8s/ci/github-deployer.yaml), verifies each one, and with --set-secrets stores
# them base64 encoded as the KUBE_CONFIG secret of their environment (stage, production, production-voi).
#
# Run with a kubectl context that has cluster-admin rights on the cluster (to apply the RBAC):
#   k8s/ci/make-kubeconfig.sh --set-secrets   # the usual way: nothing is left on disk
#   k8s/ci/make-kubeconfig.sh                 # only build + verify, the files are kept in a temp directory
#   KUBE_SERVER=https://host:6443 k8s/ci/make-kubeconfig.sh --set-secrets
#       # the API server the runners should use, when the current context goes through a proxy (e.g. Rancher)
set -euo pipefail

NAMESPACE=biatec-scan
REPO=scholtz/AVMTradeReporter
SET_SECRETS=0
OUT_DIR=""
while [ $# -gt 0 ]; do
  case "$1" in
    --set-secrets) SET_SECRETS=1; shift ;;
    --out-dir) OUT_DIR="$2"; shift 2 ;;
    *) echo "unknown argument $1" >&2; exit 64 ;;
  esac
done

# Plain functions instead of associative arrays: runs on macOS' bash 3.2, too.
ENVIRONMENTS="stage production production-voi"
# environment -> its ServiceAccount and a Deployment it must be able to patch
env_info() {
  case "$1" in
    stage) echo "github-deployer-stage|avm-trade-reporter-stage-app-deployment" ;;
    production) echo "github-deployer-production|avm-trade-reporter-app-deployment" ;;
    production-voi) echo "github-deployer-voi|avm-trade-reporter-voi-app-deployment" ;;
  esac
}
# the objects of an environment (Deployments, Secrets - production's are managed by hand -, ConfigMaps): every OTHER
# environment's credential must be denied on each of them
env_objects() {
  case "$1" in
    stage) echo "deployments.apps/avm-trade-reporter-stage-app-deployment deployments.apps/avmtradereporter-subscriber-stage
                 secrets/avm-trade-reporter-stage-secret secrets/avm-trade-reporter-subscriber-stage-secret
                 configmaps/avm-trade-reporter-stage-main-conf" ;;
    production) echo "deployments.apps/avm-trade-reporter-app-deployment deployments.apps/avm-trade-reporter2-app-deployment
                      deployments.apps/avmtradereporter-subscriber secrets/avm-trade-reporter-secret
                      secrets/avm-trade-reporter2-secret secrets/avm-trade-reporter-subscriber-secret
                      configmaps/avm-trade-reporter-main-conf" ;;
    production-voi) echo "deployments.apps/avm-trade-reporter-voi-app-deployment deployments.apps/avmtradereporter-subscriber-voi
                          secrets/avm-trade-reporter-voi-secret secrets/avm-trade-reporter-subscriber-voi-secret
                          configmaps/avm-trade-reporter-voi-main-conf" ;;
  esac
}

here=$(cd "$(dirname "$0")" && pwd)
check="$here/../../scripts/check-kubeconfig.sh"

# the kubeconfigs hold credentials: never inside the repository, removed again when they went into the secrets -
# only what this script created (its own temp directory, or just its files in a given --out-dir)
written=()
cleanup() {
  if [ -n "${OWN_DIR:-}" ]; then rm -rf "$OWN_DIR"; else rm -f ${written[@]+"${written[@]}"}; fi
}
if [ -z "$OUT_DIR" ]; then
  OUT_DIR=$(mktemp -d "${TMPDIR:-/tmp}/github-deployer.XXXXXX")
  OWN_DIR="$OUT_DIR"
else
  mkdir -p "$OUT_DIR"
fi
if [ "$SET_SECRETS" -eq 1 ]; then
  trap cleanup EXIT
fi

server="${KUBE_SERVER:-$(kubectl config view --minify -o jsonpath='{.clusters[0].cluster.server}')}"
if [[ "$server" == */k8s/clusters/* ]]; then
  echo "The current context's API server ($server) is a Rancher proxy, which does not accept ServiceAccount tokens." >&2
  echo "Set KUBE_SERVER to the cluster's own API endpoint (e.g. https://<node>:6443) and run again." >&2
  exit 1
fi

kubectl apply -f "$here/github-deployer.yaml"

for env in $ENVIRONMENTS; do
  IFS='|' read -r sa own <<< "$(env_info "$env")"
  secret="$sa-token"
  token=""
  ca=""
  # the token controller fills the Secret asynchronously
  for _ in $(seq 1 30); do
    token=$(kubectl -n "$NAMESPACE" get secret "$secret" -o jsonpath='{.data.token}' 2>/dev/null | base64 -d || true)
    ca=$(kubectl -n "$NAMESPACE" get secret "$secret" -o jsonpath='{.data.ca\.crt}' 2>/dev/null || true)
    [ -n "$token" ] && [ -n "$ca" ] && break
    sleep 1
  done
  if [ -z "$token" ] || [ -z "$ca" ]; then
    echo "$NAMESPACE/$secret has no token / ca.crt yet (is the ServiceAccount token controller enabled?)" >&2
    exit 1
  fi

  file="$OUT_DIR/$env.kubeconfig"
  written+=("$file")
  ( umask 077; cat > "$file" <<EOF
apiVersion: v1
kind: Config
clusters:
  - name: biatec
    cluster:
      server: $server
      certificate-authority-data: $ca
users:
  - name: $sa
    user:
      token: $token
contexts:
  - name: $sa@biatec
    context:
      cluster: biatec
      user: $sa
      namespace: $NAMESPACE
current-context: $sa@biatec
EOF
  )

  echo "== $env ($sa)"
  KUBECONFIG="$file" bash "$check" --namespace "$NAMESPACE" --can-i "patch deployments.apps/$own"
  denied=0
  for other in $ENVIRONMENTS; do
    [ "$other" = "$env" ] && continue
    for object in $(env_objects "$other"); do
      for verb in get patch; do
        if [ "$(KUBECONFIG="$file" kubectl auth can-i "$verb" "$object" -n "$NAMESPACE" 2>/dev/null)" != "no" ]; then
          echo "The $env deployer may '$verb $object' of $other - it must not reach another environment's objects" >&2
          exit 1
        fi
        denied=$((denied + 1))
      done
    done
  done
  echo "Denied on all $denied checks against the other environments' objects - separated."

  if [ "$SET_SECRETS" -eq 1 ]; then
    encoded=$(base64 -w0 "$file" 2>/dev/null || base64 "$file" | tr -d '\n')
    printf '%s' "$encoded" | gh secret set KUBE_CONFIG --env "$env" --repo "$REPO"
    echo "Set KUBE_CONFIG of environment $env"
  fi
done

if [ "$SET_SECRETS" -eq 1 ]; then
  echo "Done (the kubeconfigs were removed). Re-run the failed deploy: gh run rerun <run-id> --failed --repo $REPO"
else
  echo "Verified kubeconfigs in $OUT_DIR - they contain credentials: store them (base64) as KUBE_CONFIG of their"
  echo "environment and delete the directory, or simply run: $0 --set-secrets"
fi
