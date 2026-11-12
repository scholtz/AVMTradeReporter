#!/usr/bin/env bash
# Points the API / Subscriber image of each given manifest at <version>, changing nothing else in the file.
#
# Used by the deploy jobs to update the manifests before `kubectl apply`, and by .github/actions/commit-manifests to
# re-apply exactly that change on a fresh origin/master - so an edit to the same manifests that landed on master
# during the rollout (probes, limits, env) is kept, not overwritten by the job's older copy of the file.
#
# Usage: scripts/set-image-tags.sh <version> <manifest>...
#        scripts/set-image-tags.sh --current <manifest>   prints the tag of its API (else Subscriber) image
# Images: $IMAGE_API / $IMAGE_SUBSCRIBER (workflow env), defaulting to the Docker Hub names.
set -euo pipefail
if [ $# -lt 2 ]; then
  echo "usage: $0 <version> <manifest>... | $0 --current <manifest>" >&2
  exit 64
fi
version="$1"
shift
api="${IMAGE_API:-scholtz2/avm-trade-reporter}"
subscriber="${IMAGE_SUBSCRIBER:-scholtz2/avm-trade-reporter-subscriber}"
# the names are literal text in the patterns: escape the regex metacharacters (e.g. the dots of a registry host)
re_escape() { printf '%s' "$1" | sed 's/[][\.*^$+?(){}|]/\\&/g'; }
pattern_api="(image:[[:space:]]*\"?)$(re_escape "$api"):[^[:space:]\"]+"
pattern_subscriber="(image:[[:space:]]*\"?)$(re_escape "$subscriber"):[^[:space:]\"]+"
if [ "$version" = --current ]; then
  # the same exact-name patterns as the rewrite below: another image (a sidecar, an init container) never counts
  for pattern in "$pattern_api" "$pattern_subscriber"; do
    line=$(grep -oE "$pattern" "$1" | head -n1 || true)
    if [ -n "$line" ]; then
      printf '%s\n' "${line##*:}"
      exit 0
    fi
  done
  exit 1
fi
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
