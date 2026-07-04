#!/usr/bin/env bash
# Merge one build's version-entry fragment into the accumulating plugin-repository
# manifest.json (append-not-overwrite), rewriting sourceUrl to the real release asset URL.
#
# This is the heart of the stable-release distribution: each release APPENDS to the
# plugin's versions[] array so prior versions survive for history/rollback, instead of
# replacing the manifest with a single localhost-pointing entry (the original bug).
#
# Usage:
#   merge-manifest.sh <existing-manifest.json> <version-entry.json> <source-url> [out.json]
#
#   <existing-manifest.json>  the current committed manifest on gh-pages. If it does not
#                             exist or is empty, a fresh single-plugin manifest is seeded.
#   <version-entry.json>      dist/version-entry.json produced by build.sh (sourceUrl is a
#                             placeholder here).
#   <source-url>             the real GitHub Release asset download URL for this version's zip.
#   [out.json]               where to write the merged manifest (default: overwrite existing).
#
# Behaviour:
#   - Idempotent per version: if an entry with the same `version` already exists it is
#     REPLACED (re-running a release for the same version updates it, never duplicates).
#   - versions[] is sorted newest-first by semantic version so Jellyfin offers the latest.
#   - The plugin's identity fields (guid/name/etc.) are preserved from the existing manifest
#     when present, otherwise seeded from the constants below.
set -euo pipefail

EXISTING="${1:?existing manifest path required}"
ENTRY="${2:?version-entry.json path required}"
SOURCE_URL="${3:?source url required}"
OUT="${4:-$EXISTING}"

GUID="3ed77eb2-77c8-49c9-8a14-8cfcb86cb6f3"
NAME="JellyFetch"
DESCRIPTION="Download manager: submit SVT Play / YouTube / magnet / .torrent links and have them land in your library."
OVERVIEW="URL-driven download manager for Jellyfin"
OWNER="jellyfetch"
CATEGORY="General"

# Seed manifest used when there is no existing manifest yet (first release).
SEED=$(jq -n \
  --arg guid "$GUID" --arg name "$NAME" --arg desc "$DESCRIPTION" \
  --arg overview "$OVERVIEW" --arg owner "$OWNER" --arg category "$CATEGORY" \
  '[{guid:$guid, name:$name, description:$desc, overview:$overview, owner:$owner, category:$category, imageUrl:null, versions:[]}]')

if [ -s "$EXISTING" ] && jq -e 'type=="array"' "$EXISTING" >/dev/null 2>&1; then
  BASE=$(cat "$EXISTING")
else
  BASE="$SEED"
fi

# The new version entry with its real sourceUrl substituted in.
NEW_ENTRY=$(jq --arg url "$SOURCE_URL" '.sourceUrl = $url' "$ENTRY")

# Merge:
#  1. locate (or seed) the JellyFetch plugin object by guid
#  2. drop any existing version entry with the same version (idempotent replace)
#  3. append the new entry
#  4. sort versions newest-first by numeric [major,minor,patch,build]
echo "$BASE" | jq \
  --arg guid "$GUID" \
  --argjson seed "$(echo "$SEED" | jq '.[0]')" \
  --argjson entry "$NEW_ENTRY" '
  # ensure the plugin object exists
  (if any(.[]; .guid == $guid) then . else . + [$seed] end)
  | map(
      if .guid == $guid then
        .versions = (
          ((.versions // []) | map(select(.version != $entry.version))) + [$entry]
          | sort_by(.version | split(".") | map(tonumber? // 0))
          | reverse
        )
      else . end
    )
' > "$OUT.tmp"

mv "$OUT.tmp" "$OUT"
echo "Merged version $(echo "$NEW_ENTRY" | jq -r .version) into $OUT ($(jq '.[] | select(.guid==$g) | .versions | length' --arg g "$GUID" "$OUT") total versions)"
