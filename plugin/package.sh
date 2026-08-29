#!/usr/bin/env bash
# Builds a Release zip for the Jellyfin plugin and prints the manifest.json entry to
# add/update by hand. Run from anywhere; paths below are relative to this script.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

CSPROJ="Jellyfin.Plugin.TrailerFetcher/Jellyfin.Plugin.TrailerFetcher.csproj"
VERSION="$(grep -m1 '<Version>' "$CSPROJ" 2>/dev/null | sed -E 's/.*<Version>(.*)<\/Version>.*/\1/' || true)"
if [ -z "$VERSION" ]; then
    # No <Version> in the csproj - fall back to build.yaml's version field.
    VERSION="$(grep -m1 '^version:' build.yaml | sed -E 's/version: *"(.*)"/\1/')"
fi

echo "Packaging version $VERSION..."

# Clean rebuild - dotnet's incremental build can otherwise skip recompiling on rapid
# successive edits (observed: an unchanged-checksum zip after real code changes).
rm -rf publish dist Jellyfin.Plugin.TrailerFetcher/bin Jellyfin.Plugin.TrailerFetcher/obj
dotnet publish "$CSPROJ" -c Release -o publish

mkdir -p dist
ZIP_NAME="Jellyfin.Plugin.TrailerFetcher_${VERSION}.zip"
ZIP_PATH="dist/${ZIP_NAME}"

# Only the plugin's own assembly (+ pdb for stack traces) ships - everything else
# (Jellyfin.Controller/Model/Common, ASP.NET Core) is provided by the Jellyfin host.
(cd publish && zip -q "../${ZIP_PATH}" Jellyfin.Plugin.TrailerFetcher.dll Jellyfin.Plugin.TrailerFetcher.pdb)

CHECKSUM="$(md5 -q "$ZIP_PATH" 2>/dev/null || md5sum "$ZIP_PATH" | cut -d' ' -f1)"
TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

echo
echo "Built: $ZIP_PATH"
echo "Checksum (MD5): $CHECKSUM"
echo
echo "Add/replace this entry in manifest.json's \"versions\" array:"
cat <<EOF
    {
      "version": "$VERSION",
      "changelog": "See commit history.",
      "targetAbi": "10.10.0.0",
      "sourceUrl": "https://codeberg.org/grexe/jellyfin-trailer-fetcher/raw/branch/plugin/plugin/dist/${ZIP_NAME}",
      "checksum": "$CHECKSUM",
      "timestamp": "$TIMESTAMP"
    }
EOF
