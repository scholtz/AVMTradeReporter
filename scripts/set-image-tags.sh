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
pattern_api="(image:[[:space:]]*\"?)${api}:[^[:space:]\"]+"
pattern_subscriber="(image:[[:space:]]*\"?)${subscriber}:[^[:space:]\"]+"
for manifest in "$@"; do
  # a manifest whose image line has another shape (registry prefix, digest, no tag) would silently keep the old image
  if ! grep -qE "$pattern_api|$pattern_subscriber" "$manifest"; then
    echo "::error::$manifest has no 'image: ${api}:<tag>' or 'image: ${subscriber}:<tag>' line to point at $version" >&2
    exit 1
  fi
  # "<image>:<tag>" exactly: the subscriber image name starts with the API one, the ':' tells them apart
  sed -i -E \
    -e "s~${pattern_subscriber}~\1${subscriber}:${version}~" \
    -e "s~${pattern_api}~\1${api}:${version}~" \
    "$manifest"
done
