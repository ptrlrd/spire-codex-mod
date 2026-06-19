#!/usr/bin/env bash
# Stage the built mod files into the Steam Workshop workspace (workshop/content/), then upload
# with MegaCrit's official uploader (https://github.com/megacrit/sts2-mod-uploader):
#   ModUploader.exe upload -w workshop
# Steam must be running and signed in (the uploader needs an authenticated Steam session, which
# is why this can't run in CI like the Nexus upload). Close the game first: package.sh also
# copies into the live mods folder.
set -euo pipefail
cd "$(dirname "$0")/.."

bash tools/package.sh

CONTENT=workshop/content
rm -rf "$CONTENT"
mkdir -p "$CONTENT"
cp dist/SpireCodex/SpireCodex.json "$CONTENT/"
cp dist/SpireCodex/SpireCodex.dll  "$CONTENT/"
cp dist/SpireCodex/SpireCodex.pck  "$CONTENT/"

echo "Staged Workshop content in $CONTENT/. Now run: ModUploader.exe upload -w workshop"
