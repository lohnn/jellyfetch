#!/usr/bin/env bash
# Compute the next stable version = last-released version + bump.
#
# "Last released" is resolved in this order:
#   1. the highest `version` in the plugin's versions[] on the committed gh-pages manifest
#      (the authoritative record of what has actually shipped), if a manifest is given and
#      has entries;
#   2. otherwise the csproj <Version> fallback passed as the 3rd arg (bootstrap / first release).
#
# Versions use Jellyfin's 4-component form major.minor.patch.build. We bump the semantic
# part and keep .build at 0:
#   patch -> major.minor.(patch+1).0
#   minor -> major.(minor+1).0.0
#   major -> (major+1).0.0.0
#
# Usage: next-version.sh <bump: patch|minor|major> <manifest.json|-> <csproj-fallback-version>
# Prints the next version to stdout.
set -euo pipefail

BUMP="${1:?bump (patch|minor|major) required}"
MANIFEST="${2:-}"
FALLBACK="${3:?csproj fallback version required}"

GUID="3ed77eb2-77c8-49c9-8a14-8cfcb86cb6f3"

LAST=""
if [ -n "$MANIFEST" ] && [ "$MANIFEST" != "-" ] && [ -s "$MANIFEST" ] && jq -e 'type=="array"' "$MANIFEST" >/dev/null 2>&1; then
  # Highest version among all entries for our plugin (robust even if unsorted on disk).
  LAST=$(jq -r --arg g "$GUID" '
    [ .[] | select(.guid==$g) | .versions[]?.version ]
    | map(split(".") | map(tonumber? // 0))
    | sort | reverse | .[0] // empty
    | map(tostring) | join(".")
  ' "$MANIFEST")
fi

if [ -z "$LAST" ]; then
  LAST="$FALLBACK"
fi

# Normalise to 4 components.
IFS='.' read -r MA MI PA BU <<<"$LAST"
MA=${MA:-0}; MI=${MI:-0}; PA=${PA:-0}; BU=${BU:-0}

case "$BUMP" in
  patch) PA=$((PA+1));;
  minor) MI=$((MI+1)); PA=0;;
  major) MA=$((MA+1)); MI=0; PA=0;;
  *) echo "unknown bump: $BUMP" >&2; exit 1;;
esac
BU=0

echo "$MA.$MI.$PA.$BU"
