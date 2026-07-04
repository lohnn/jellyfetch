#!/usr/bin/env bash
# Builds the JellyFetch plugin zip + plugin-repository manifest.json into dist/.
# Usage: ./build.sh [version]   (version defaults to the csproj <Version>)
set -euo pipefail
cd "$(dirname "$0")"

# Local dev-host shim only (this box installs the SDK outside PATH); CI provisions
# dotnet via actions/setup-dotnet and this directory simply won't exist there.
if [ -d /usr/local/dotnet ]; then
    export PATH=/usr/local/dotnet:$PATH
fi

PROJ=src/Jellyfetch.Plugin/Jellyfetch.Plugin.csproj
VERSION="${1:-$(grep -oPm1 '(?<=<Version>)[^<]+' "$PROJ")}"
TARGET_ABI="10.11.0.0"
GUID="3ed77eb2-77c8-49c9-8a14-8cfcb86cb6f3"
NAME="JellyFetch"
OUT="dist"
STAGE="$OUT/stage"

rm -rf "$OUT"
mkdir -p "$STAGE"

dotnet publish "$PROJ" -c Release -o "$STAGE/publish" -p:Version="$VERSION" --nologo

# Plugin payload = EVERY DLL that `dotnet publish` emitted, verbatim.
#
# Why copy all of them and NOT filter by name: the Jellyfin refs are marked
# ExcludeAssets=runtime in the csproj, so publish already omits every server-provided
# assembly (Jellyfin.*, MediaBrowser.*, and the shared-framework Microsoft.*/System.*)
# from this folder. What remains is EXACTLY the plugin's private third-party closure —
# all of it required at load time.
#
# A previous version stripped anything named Microsoft.*/System.* here. That was a
# latent server-crash bug: real third-party dependencies are frequently published under
# those names (e.g. System.Linq.Async, Microsoft.Extensions.Http, System.Threading.Tasks.Dataflow
# when the server doesn't provide it). Deleting them yields a plugin that throws
# FileNotFoundException/ReflectionTypeLoadException on load and takes the server's
# startup down. Never reintroduce a name-prefix blocklist here — trust publish's output.
find "$STAGE/publish" -maxdepth 1 -name '*.dll' -exec cp {} "$STAGE/" \;

# Sanity guard: the plugin's own assembly must be present, and there must be nothing
# obviously server-provided left in the payload (defense in depth against a future
# csproj change that forgets ExcludeAssets=runtime).
if [ ! -f "$STAGE/Jellyfetch.Plugin.dll" ]; then
    echo "ERROR: Jellyfetch.Plugin.dll missing from publish output" >&2
    exit 1
fi
if ls "$STAGE"/Jellyfin.*.dll "$STAGE"/MediaBrowser.*.dll >/dev/null 2>&1; then
    echo "ERROR: server-provided Jellyfin/MediaBrowser assemblies leaked into the payload." >&2
    echo "       Check ExcludeAssets=runtime on the Jellyfin package references." >&2
    exit 1
fi
rm -rf "$STAGE/publish"

echo "Payload DLLs ($(ls "$STAGE"/*.dll | wc -l)):"
ls "$STAGE"/*.dll | xargs -n1 basename | sed 's/^/  /'

TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

cat > "$STAGE/meta.json" <<EOF
{
  "guid": "$GUID",
  "name": "$NAME",
  "description": "Download manager: submit SVT Play / YouTube / magnet / .torrent links and have them land in your library.",
  "overview": "URL-driven download manager for Jellyfin",
  "owner": "jellyfetch",
  "category": "General",
  "version": "$VERSION",
  "targetAbi": "$TARGET_ABI",
  "framework": "net9.0",
  "changelog": "",
  "status": "Active",
  "autoUpdate": true,
  "imagePath": "",
  "timestamp": "$TIMESTAMP"
}
EOF

ZIP="$OUT/jellyfetch_$VERSION.zip"
(cd "$STAGE" && zip -q -r "../$(basename "$ZIP")" .)
rm -rf "$STAGE"

MD5=$(md5sum "$ZIP" | cut -d' ' -f1)

# ---------------------------------------------------------------------------
# Version-entry FRAGMENT — the single source of truth for this build's metadata.
#
# build.sh owns: the versioned zip, its MD5 checksum, and this ONE version entry.
# The stable-release workflow (plugin-release.yml) owns MERGING this fragment into
# the accumulating manifest.json on gh-pages and rewriting `sourceUrl` to the real
# GitHub Release asset URL for this version.
#
# sourceUrl here is a placeholder the workflow substitutes. CHANGELOG comes from the
# dispatch input via the CHANGELOG env var (empty by default for a plain local build).
CHANGELOG="${CHANGELOG:-}"
# JSON-escape the changelog (quotes, backslashes, newlines) so freetext can't break JSON.
CHANGELOG_JSON=$(printf '%s' "$CHANGELOG" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')

cat > "$OUT/version-entry.json" <<EOF
{
  "version": "$VERSION",
  "changelog": $CHANGELOG_JSON,
  "targetAbi": "$TARGET_ABI",
  "sourceUrl": "SOURCE_URL_PLACEHOLDER",
  "checksum": "$MD5",
  "timestamp": "$TIMESTAMP"
}
EOF

# ---------------------------------------------------------------------------
# Standalone single-entry manifest.json — for LOCAL install/testing only.
#
# This is a convenience so `./build.sh` alone yields something addable to a local
# Jellyfin. The PUBLISHED, accumulating manifest lives on gh-pages and is produced by
# the workflow's merge step — NOT this file. sourceUrl defaults to the real GitHub
# Release asset URL shape so even the local manifest is realistic; override with
# BASE_URL (e.g. a `python3 -m http.server` dev host) to point at a local zip instead.
OWNER_REPO="${GITHUB_REPOSITORY:-lohnn/jellyfetch}"
BASE_URL="${BASE_URL:-https://github.com/$OWNER_REPO/releases/download/v$VERSION}"
cat > "$OUT/manifest.json" <<EOF
[
  {
    "guid": "$GUID",
    "name": "$NAME",
    "description": "Download manager: submit SVT Play / YouTube / magnet / .torrent links and have them land in your library.",
    "overview": "URL-driven download manager for Jellyfin",
    "owner": "jellyfetch",
    "category": "General",
    "imageUrl": null,
    "versions": [
      {
        "version": "$VERSION",
        "changelog": $CHANGELOG_JSON,
        "targetAbi": "$TARGET_ABI",
        "sourceUrl": "$BASE_URL/jellyfetch_$VERSION.zip",
        "checksum": "$MD5",
        "timestamp": "$TIMESTAMP"
      }
    ]
  }
]
EOF

echo "Built: $ZIP (md5 $MD5)"
echo "Version entry: $OUT/version-entry.json (sourceUrl placeholder — workflow substitutes)"
echo "Local manifest: $OUT/manifest.json (BASE_URL=$BASE_URL)"
