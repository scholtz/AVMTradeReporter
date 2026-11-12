#!/usr/bin/env bash
# Preflight of the deploy jobs: every object a job is about to change, or that its pods need, must already exist -
# the deployer Roles (k8s/ci/github-deployer.yaml) cannot create objects - and, for "patch:", be patchable. Runs before
# anything in the cluster changes, so a missing object or permission fails the job with a clear reason instead of
# half-way through the deploy.
#
# Usage: NAMESPACE=<ns> scripts/check-objects.sh <get|patch>:<resource>/<name>...
#   e.g. patch:configmaps/avm-trade-reporter-stage-main-conf get:persistentvolumeclaims/avm-trade-reporter-images-pvc
# Exit codes: 0 ok, 1 an object is missing / unreadable / not patchable, 64 usage.
set -euo pipefail
if [ $# -eq 0 ] || [ -z "${NAMESPACE:-}" ]; then
  echo "usage: NAMESPACE=<ns> $0 <get|patch>:<resource>/<name>..." >&2
  exit 64
fi
failed=0
for spec in "$@"; do
  verb="${spec%%:*}"
  object="${spec#*:}"
  if [ "$verb" = "$spec" ] || { [ "$verb" != get ] && [ "$verb" != patch ]; }; then
    echo "usage: '$spec' is not <get|patch>:<resource>/<name>" >&2
    exit 64
  fi
  if ! out=$(kubectl get "$object" -n "$NAMESPACE" -o name --request-timeout=30s 2>&1); then
    if printf '%s' "$out" | grep -q 'NotFound'; then
      hint=""
      [[ "$object" == persistentvolumeclaims/* ]] && hint=" (the shared images PVC: k8s/main/pv-images.yaml)"
      echo "::error::$object does not exist in $NAMESPACE - the deployers cannot create objects, an admin must create it once$hint. See docs/CICD_GITHUB_ACTIONS.md, 'Limits by design'."
    else
      echo "::error::Could not read $object in $NAMESPACE: $(printf '%s' "$out" | tail -1)"
    fi
    failed=1
    continue
  fi
  if [ "$verb" = patch ] && [ "$(kubectl auth can-i patch "$object" -n "$NAMESPACE" 2>/dev/null || true)" != yes ]; then
    echo "::error::The deployer may not patch $object in $NAMESPACE - add it to the job's Role in k8s/ci/github-deployer.yaml and re-run k8s/ci/make-kubeconfig.sh."
    failed=1
    continue
  fi
  echo "ok: $verb $object"
done
exit "$failed"
