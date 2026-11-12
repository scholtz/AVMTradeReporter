#!/usr/bin/env bash
# Builds the kubeconfigs of the GitHub Actions deploy jobs from the per-environment deployer ServiceAccounts
# (non-expiring, revocable tokens - see k8s/ci/github-deployer.yaml), verifies each one, and with --set-secrets stores
# them base64 encoded as the KUBE_CONFIG secret of their environment (production, production-voi). Stage is not
# managed here: its KUBE_CONFIG is scoped to the namespace biatec-scan-testnet (created by the cluster admin).
#
# Run with a kubectl context that has cluster-admin rights on the cluster (to apply the RBAC):
#   k8s/ci/make-kubeconfig.sh --set-secrets   # the usual way: nothing is left on disk
#   k8s/ci/make-kubeconfig.sh                 # only build + verify, the files are kept in a temp directory
#   KUBE_SERVER=https://host:6443 k8s/ci/make-kubeconfig.sh --set-secrets
#       # the API server the runners should use, when the current context goes through a proxy (e.g. Rancher)
set -euo pipefail

NAMESPACE=biatec-scan
DEPLOYER_NAMESPACE=github-deployers # the ServiceAccounts + token Secrets (see github-deployer.yaml)
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
ENVIRONMENTS="production production-voi"
# environment -> its ServiceAccount and a Deployment it must be able to patch
env_info() {
  case "$1" in
    production) echo "github-deployer-production|avm-trade-reporter-app-deployment" ;;
    production-voi) echo "github-deployer-voi|avm-trade-reporter-voi-app-deployment" ;;
  esac
}
# the objects of an environment: every object its Role names (read from the APPLIED Role, so the list cannot drift
# from github-deployer.yaml) plus production's hand-managed Secrets. Every OTHER environment's credential must be
# denied on each of them. The shared icon cache PVC is left out: every deployer may read it.
env_objects() {
  local sa group resource names n
  IFS='|' read -r sa _ <<< "$(env_info "$1")"
  # one line per rule: API groups | resources | names (each space separated; the core group is "")
  kubectl -n "$NAMESPACE" get role "$sa" \
    -o jsonpath='{range .rules[*]}{.apiGroups[*]}{"|"}{.resources[*]}{"|"}{.resourceNames[*]}{"\n"}{end}' |
    while IFS='|' read -r groups resources names; do
      [ -n "$groups" ] || groups="-"
      for group in $groups; do
        [ "$group" = "-" ] && group=""
        for resource in $resources; do
          [ "$resource" = persistentvolumeclaims ] && continue # checked separately below
          if [ -z "$names" ]; then echo "UNRESTRICTED:$resource"; continue; fi
          for n in $names; do echo "$resource${group:+.$group}/$n"; done
        done
      done
    done
  if [ "$1" = production ]; then
    echo "secrets/avm-trade-reporter-secret secrets/avm-trade-reporter2-secret secrets/avm-trade-reporter-subscriber-secret"
  fi
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
    token_b64=$(kubectl -n "$DEPLOYER_NAMESPACE" get secret "$secret" -o jsonpath='{.data.token}' 2>/dev/null || true)
    # (older macOS base64 only knows -D)
    token=$(printf '%s' "$token_b64" | base64 -d 2>/dev/null || printf '%s' "$token_b64" | base64 -D 2>/dev/null || true)
    ca=$(kubectl -n "$DEPLOYER_NAMESPACE" get secret "$secret" -o jsonpath='{.data.ca\.crt}' 2>/dev/null || true)
    [ -n "$token" ] && [ -n "$ca" ] && break
    sleep 1
  done
  if [ -z "$token" ] || [ -z "$ca" ]; then
    echo "$DEPLOYER_NAMESPACE/$secret has no token / ca.crt yet (is the ServiceAccount token controller enabled?)" >&2
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
    objects=$(env_objects "$other") || objects="" # kubectl's own error is printed above; explained below
    if [ -z "$objects" ] || [[ "$objects" == *UNRESTRICTED:* ]]; then
      echo "Role of $other is missing or has a rule without resourceNames - every rule must name its objects" >&2
      exit 1
    fi
    for object in $objects; do
      for verb in get patch update delete; do
        if [ "$(KUBECONFIG="$file" kubectl auth can-i "$verb" "$object" -n "$NAMESPACE" 2>/dev/null)" != "no" ]; then
          echo "The $env deployer may '$verb $object' of $other - it must not reach another environment's objects" >&2
          exit 1
        fi
        denied=$((denied + 1))
      done
    done
  done
  # nothing broad, whatever the Roles say: no listing / creating, no RBAC changes
  for perm in "list secrets" "watch secrets" "create secrets" "create pods" "create deployments.apps" \
               "create serviceaccounts" "create rolebindings.rbac.authorization.k8s.io" "patch roles.rbac.authorization.k8s.io"; do
    # shellcheck disable=SC2086 # "verb resource"
    if [ "$(KUBECONFIG="$file" kubectl auth can-i $perm -n "$NAMESPACE" 2>/dev/null)" != "no" ]; then
      echo "The $env deployer may '$perm' - Roles must stay get/patch on named objects" >&2
      exit 1
    fi
    denied=$((denied + 1))
  done
  # no deployer reaches the deployer identities (another environment's token) in any way
  for perm in "get secrets" "list secrets" "create pods" "create serviceaccounts --subresource=token"; do
    # shellcheck disable=SC2086 # "verb resource"
    if [ "$(KUBECONFIG="$file" kubectl auth can-i $perm -n "$DEPLOYER_NAMESPACE" 2>/dev/null)" != "no" ]; then
      echo "The $env deployer may '$perm' in $DEPLOYER_NAMESPACE - the deployer tokens must be out of every deployer's reach" >&2
      exit 1
    fi
    denied=$((denied + 1))
  done
  # the shared icon cache that production's API pods mount: only production may change it
  if [ "$env" != production ]; then
    for perm in "patch persistentvolumeclaims/avm-trade-reporter-images-pvc" "delete persistentvolumeclaims/avm-trade-reporter-images-pvc" \
                 "patch persistentvolumes/avm-trade-reporter-images-pv" "delete persistentvolumes/avm-trade-reporter-images-pv"; do
      # shellcheck disable=SC2086 # "verb resource/name"
      if [ "$(KUBECONFIG="$file" kubectl auth can-i $perm -n "$NAMESPACE" 2>/dev/null)" != "no" ]; then
        echo "The $env deployer may '$perm' - only production may change the shared images volume" >&2
        exit 1
      fi
      denied=$((denied + 1))
    done
  fi
  echo "Denied on all $denied checks against the other environments' objects and broad / shared-volume access - separated."
done

# only after BOTH verified: a verification failure above leaves every environment's secret as it was. A failing
# `gh secret set` (permissions, network) is reported with which environments already got their new secret - re-running
# the script is safe: it re-verifies and sets all three again.
if [ "$SET_SECRETS" -eq 1 ]; then
  done_envs=""
  for env in $ENVIRONMENTS; do
    file="$OUT_DIR/$env.kubeconfig"
    encoded=$(base64 -w0 "$file" 2>/dev/null || base64 "$file" | tr -d '\n')
    if ! printf '%s' "$encoded" | gh secret set KUBE_CONFIG --env "$env" --repo "$REPO"; then
      echo "Setting KUBE_CONFIG of environment $env FAILED. Already set: ${done_envs:-none}. Fix the cause and run the script again." >&2
      exit 1
    fi
    done_envs="${done_envs:+$done_envs }$env"
    echo "Set KUBE_CONFIG of environment $env"
  done
  echo "Done (the kubeconfigs were removed). They are used by the next production / Voi promote (promote-production.yml)."
else
  echo "Verified kubeconfigs in $OUT_DIR - they contain credentials: store them (base64) as KUBE_CONFIG of their"
  echo "environment and delete the directory, or simply run: $0 --set-secrets"
fi
