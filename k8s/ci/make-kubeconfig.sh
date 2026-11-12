#!/usr/bin/env bash
# Builds the kubeconfig of the GitHub Actions deploy jobs from the github-deployer ServiceAccount (non-expiring,
# revocable token - see k8s/ci/github-deployer.yaml) and, with --set-secrets, stores it base64 encoded as the
# KUBE_CONFIG secret of the stage, production and production-voi environments.
#
# Run with a kubectl context that has cluster-admin rights on the cluster (to apply the RBAC once):
#   k8s/ci/make-kubeconfig.sh                  # writes ./github-deployer.kubeconfig and verifies it
#   k8s/ci/make-kubeconfig.sh --set-secrets    # ... and sets the three GitHub environment secrets (needs gh auth)
#   KUBE_SERVER=https://host:6443 k8s/ci/make-kubeconfig.sh   # API server the runners should use, when the current
#                                                             # context goes through a proxy (e.g. Rancher)
# The kubeconfig contains a credential: the file is created mode 600 and should be deleted after use.
set -euo pipefail

NAMESPACE=biatec-scan
SA=github-deployer
TOKEN_SECRET=github-deployer-token
REPO=scholtz/AVMTradeReporter
OUT=github-deployer.kubeconfig
SET_SECRETS=0
while [ $# -gt 0 ]; do
  case "$1" in
    --set-secrets) SET_SECRETS=1; shift ;;
    --out) OUT="$2"; shift 2 ;;
    *) echo "unknown argument $1" >&2; exit 64 ;;
  esac
done

here=$(cd "$(dirname "$0")" && pwd)
kubectl apply -f "$here/github-deployer.yaml"

# the token controller fills the Secret asynchronously
token=""
for _ in $(seq 1 30); do
  token=$(kubectl -n "$NAMESPACE" get secret "$TOKEN_SECRET" -o jsonpath='{.data.token}' 2>/dev/null | base64 -d || true)
  [ -n "$token" ] && break
  sleep 1
done
if [ -z "$token" ]; then
  echo "The token of $NAMESPACE/$TOKEN_SECRET was not populated (is the ServiceAccount token controller enabled?)" >&2
  exit 1
fi
ca=$(kubectl -n "$NAMESPACE" get secret "$TOKEN_SECRET" -o jsonpath='{.data.ca\.crt}')
server="${KUBE_SERVER:-$(kubectl config view --minify -o jsonpath='{.clusters[0].cluster.server}')}"
if [[ "$server" == */k8s/clusters/* ]]; then
  echo "The current context's API server ($server) is a Rancher proxy, which does not accept ServiceAccount tokens." >&2
  echo "Set KUBE_SERVER to the cluster's own API endpoint (e.g. https://<node>:6443) and run again." >&2
  exit 1
fi

umask 077
cat > "$OUT" <<EOF
apiVersion: v1
kind: Config
clusters:
  - name: biatec
    cluster:
      server: $server
      certificate-authority-data: $ca
users:
  - name: $SA
    user:
      token: $token
contexts:
  - name: $SA@biatec
    context:
      cluster: biatec
      user: $SA
      namespace: $NAMESPACE
current-context: $SA@biatec
EOF
echo "Wrote $OUT"

# the same check the deploy jobs run first
KUBECONFIG="$OUT" "$here/../../scripts/check-kubeconfig.sh" --namespace "$NAMESPACE"
for check in "patch deployments.apps" "create secrets" "patch configmaps" "patch ingresses.networking.k8s.io" \
             "patch persistentvolumeclaims" "patch persistentvolumes/avm-trade-reporter-images-pv"; do
  # shellcheck disable=SC2086
  if [ "$(KUBECONFIG="$OUT" kubectl auth can-i $check -n "$NAMESPACE")" != "yes" ]; then
    echo "The deployer may not: $check" >&2
    exit 1
  fi
done
echo "The deployer has every permission the workflows need."

if [ "$SET_SECRETS" -eq 1 ]; then
  encoded=$(base64 -w0 "$OUT" 2>/dev/null || base64 "$OUT" | tr -d '\n')
  for env in stage production production-voi; do
    printf '%s' "$encoded" | gh secret set KUBE_CONFIG --env "$env" --repo "$REPO"
    echo "Set KUBE_CONFIG of environment $env"
  done
  echo "Done - delete $OUT. Re-run the failed deploy: gh run rerun <run-id> --failed --repo $REPO"
else
  echo "Next: store it (base64) as KUBE_CONFIG in the stage, production and production-voi environments, e.g."
  echo "  $0 --set-secrets"
fi
