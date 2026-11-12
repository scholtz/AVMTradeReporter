#!/usr/bin/env bash
# Points the API / Subscriber image of each given manifest at <version>, changing nothing else in the file.
#
# Used by the deploy jobs to update the manifests before `kubectl apply`, and by .github/actions/commit-manifests to
# re-apply exactly that change on a fresh origin/master - so an edit to the same manifests that landed on master
# during the rollout (probes, limits, env) is kept, not overwritten by the job's older copy of the file.
#
# Usage: scripts/set-image-tags.sh <version> <manifest>...
# Images: $IMAGE_API / $IMAGE_SUBSCRIBER (workflow env), defaulting to the Docker Hub names.
set -euo pipefail
if [ $# -lt 2 ]; then
  echo "usage: $0 <version> <manifest>..." >&2
  exit 64
fi
version="$1"
shift
api="${IMAGE_API:-scholtz2/avm-trade-reporter}"
subscriber="${IMAGE_SUBSCRIBER:-scholtz2/avm-trade-reporter-subscriber}"
for manifest in "$@"; do
  # "<image>:<tag>" exactly: the subscriber image name starts with the API one, the ':' tells them apart
  sed -i -E \
    -e "s~(image:[[:space:]]*\"?)${subscriber}:[^[:space:]\"]+~\1${subscriber}:${version}~" \
    -e "s~(image:[[:space:]]*\"?)${api}:[^[:space:]\"]+~\1${api}:${version}~" \
    "$manifest"
done
