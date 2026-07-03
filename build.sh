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

# Plugin payload: our DLL + any non-Jellyfin dependencies (Jellyfin.* are ExcludeAssets=runtime,
# framework assemblies come from the server).
cp "$STAGE/publish/Jellyfetch.Plugin.dll" "$STAGE/"
find "$STAGE/publish" -maxdepth 1 -name '*.dll' ! -name 'Jellyfetch.Plugin.dll' \
    ! -name 'Jellyfin.*' ! -name 'MediaBrowser.*' ! -name 'Microsoft.*' ! -name 'System.*' \
    -exec cp {} "$STAGE/" \;
rm -rf "$STAGE/publish"

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

# Plugin repository manifest — host dist/ somewhere and point Jellyfin at manifest.json.
# sourceUrl below is a placeholder; substitute your hosting URL (BASE_URL env) when publishing.
BASE_URL="${BASE_URL:-http://localhost:8000}"
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
        "changelog": "",
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
echo "Repo manifest: $OUT/manifest.json (BASE_URL=$BASE_URL)"
