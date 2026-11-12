#!/usr/bin/env bash
# Checks the kubeconfig the deploy jobs use (KUBE_CONFIG secret, written to $KUBECONFIG / ~/.kube/config) BEFORE
# anything is applied, and explains a failure instead of letting the first kubectl call die with
#   "failed to download openapi: the server has asked for the client to provide credentials"
# (= HTTP 401: the cluster rejected the credential - in practice an expired / revoked token or certificate).
#
# Prints only non-secret facts: the API server host, the kind of credential and its expiry. Never the credential.
#
# Usage: scripts/check-kubeconfig.sh [--namespace NS] [--warn-days N]
#   --warn-days N  warn (GitHub ::warning) when the credential expires within N days (default 14);
#                  exit 2 when it expires within N days and --fail-on-warn is given (scheduled check)
set -euo pipefail

NAMESPACE="${NAMESPACE:-biatec-scan}"
WARN_DAYS=14
FAIL_ON_WARN=0
while [ $# -gt 0 ]; do
  case "$1" in
    --namespace) NAMESPACE="$2"; shift 2 ;;
    --warn-days) WARN_DAYS="$2"; shift 2 ;;
    --fail-on-warn) FAIL_ON_WARN=1; shift ;;
    *) echo "unknown argument $1" >&2; exit 64 ;;
  esac
done

REMEDY="Regenerate the deployer kubeconfig with k8s/ci/make-kubeconfig.sh (non-expiring ServiceAccount token) and update the KUBE_CONFIG secret of the stage, production and production-voi environments - see docs/CICD_GITHUB_ACTIONS.md, 'Deployer credentials'."

# --- what kind of credential is it, and when does it expire (from the kubeconfig itself, no cluster call) ---
server=$(kubectl config view --minify -o jsonpath='{.clusters[0].cluster.server}' 2>/dev/null || true)
user_json=$(kubectl config view --minify --raw -o jsonpath='{.users[0].user}' 2>/dev/null || true)
if [ -z "$server" ] || [ -z "$user_json" ]; then
  echo "::error::The kubeconfig is empty or unreadable (is the KUBE_CONFIG secret set and base64 encoded?). $REMEDY"
  exit 1
fi
echo "API server: ${server%%\?*}"

expires_epoch=""
kind="unknown"
token=$(kubectl config view --minify --raw -o jsonpath='{.users[0].user.token}' 2>/dev/null || true)
cert_data=$(kubectl config view --minify --raw -o jsonpath='{.users[0].user.client-certificate-data}' 2>/dev/null || true)
exec_cmd=$(kubectl config view --minify --raw -o jsonpath='{.users[0].user.exec.command}' 2>/dev/null || true)
auth_provider=$(kubectl config view --minify --raw -o jsonpath='{.users[0].user.auth-provider.name}' 2>/dev/null || true)

if [ -n "$token" ]; then
  if [[ "$token" == *.*.* ]]; then
    # a JWT (ServiceAccount token): the payload is not secret (issuer, subject, expiry), the signature is never printed
    payload=$(printf '%s' "$token" | cut -d. -f2 | tr '_-' '/+')
    while [ $(( ${#payload} % 4 )) -ne 0 ]; do payload="${payload}="; done
    claims=$(printf '%s' "$payload" | base64 -d 2>/dev/null || true)
    sub=$(printf '%s' "$claims" | jq -r '.sub // empty' 2>/dev/null || true)
    exp=$(printf '%s' "$claims" | jq -r '.exp // empty' 2>/dev/null || true)
    kind="ServiceAccount token (JWT) for ${sub:-unknown subject}"
    if [ -n "$exp" ]; then expires_epoch="$exp"; else kind="$kind, no expiry (long-lived)"; fi
  elif [[ "$token" == kubeconfig-u-* || "$token" == token-* ]]; then
    kind="Rancher API token (its lifetime is set by Rancher - default kubeconfig token TTL is 30 days)"
  else
    kind="opaque bearer token (lifetime set by the issuer, not visible here)"
  fi
elif [ -n "$cert_data" ]; then
  not_after=$(printf '%s' "$cert_data" | base64 -d | openssl x509 -noout -enddate 2>/dev/null | cut -d= -f2 || true)
  subject=$(printf '%s' "$cert_data" | base64 -d | openssl x509 -noout -subject 2>/dev/null | sed 's/^subject=//' || true)
  kind="client certificate (${subject:-unknown subject})"
  if [ -n "$not_after" ]; then expires_epoch=$(date -d "$not_after" +%s 2>/dev/null || true); fi
elif [ -n "$exec_cmd" ]; then
  kind="exec credential plugin '$exec_cmd' (needs that binary and its login on the runner)"
elif [ -n "$auth_provider" ]; then
  kind="auth-provider '$auth_provider' (deprecated in kubectl, usually needs an interactive login)"
fi
echo "Credential: $kind"

warn=0
if [ -n "$expires_epoch" ]; then
  now=$(date +%s)
  days_left=$(( (expires_epoch - now) / 86400 ))
  expires_at=$(date -u -d "@$expires_epoch" +%Y-%m-%dT%H:%M:%SZ)
  if [ "$expires_epoch" -le "$now" ]; then
    echo "::error::The deployer credential EXPIRED at $expires_at. $REMEDY"
    exit 1
  fi
  echo "Expires: $expires_at ($days_left days left)"
  if [ "$days_left" -lt "$WARN_DAYS" ]; then
    echo "::warning::The deployer credential expires in $days_left days ($expires_at). $REMEDY"
    warn=1
  fi
fi

# --- does the cluster accept it, and may it deploy? ---
if ! output=$(kubectl auth can-i patch deployments.apps -n "$NAMESPACE" --request-timeout=30s 2>&1); then
  if printf '%s' "$output" | grep -qiE "provide credentials|Unauthorized|401"; then
    echo "::error::The cluster REJECTED the deployer credential (HTTP 401 Unauthorized - expired or revoked: $kind). $REMEDY"
  elif [ "$output" = "no" ]; then
    echo "::error::The deployer credential is valid but may not patch Deployments in namespace $NAMESPACE (RBAC). Apply k8s/ci/github-deployer.yaml."
  else
    echo "::error::Could not reach / authenticate to the cluster: $output"
  fi
  exit 1
fi
echo "Cluster accepts the credential; it may patch Deployments in $NAMESPACE."

if [ "$warn" -eq 1 ] && [ "$FAIL_ON_WARN" -eq 1 ]; then
  exit 2
fi
