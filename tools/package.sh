#!/usr/bin/env bash
# Packages a distributable SpireCodex zip for Nexus / mod.io: dll + manifest + .pck.
# The zip contains a SpireCodex/ folder, so players extract it straight into <game>/mods/.
# Uses `dotnet publish` so the .pck (the loc table -> readable settings labels) is built via
# Godot. Requires GodotPath in Directory.Build.props (a Godot/MegaDot 4.5.1 .NET exe).
# The game must be CLOSED: publish also copies into the live mods folder.
set -euo pipefail
cd "$(dirname "$0")/.."

# WSL dev uses the Windows dotnet (dotnet.exe); CI on a native runner sets DOTNET=dotnet.
DOTNET="${DOTNET:-dotnet.exe}"

VERSION=$(python3 -c "import json; print(json.load(open('SpireCodex.json'))['version'])")
echo "Packaging SpireCodex $VERSION"

# Keep Godot from scanning the build/staging dir when it exports the .pck.
mkdir -p dist && : > dist/.gdignore

# Build the dll and export the .pck (both land in the build output dir).
"$DOTNET" publish SpireCodex.csproj -c ExportRelease

OUT=.godot/mono/temp/bin/ExportRelease
if [ ! -f "$OUT/SpireCodex.pck" ]; then
    echo "ERROR: $OUT/SpireCodex.pck not found. Did the Godot export run? Check GodotPath in Directory.Build.props." >&2
    exit 1
fi

STAGE=dist/SpireCodex
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp "$OUT/SpireCodex.dll" "$STAGE/"
cp SpireCodex.json "$STAGE/"
cp "$OUT/SpireCodex.pck" "$STAGE/"

OUT="dist/SpireCodex-$VERSION.zip"
rm -f "$OUT"
python3 - "$OUT" <<'EOF'
import sys, zipfile, os
with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk("dist/SpireCodex"):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, "dist"))
EOF
echo "Wrote $OUT (dll + json + pck; extract into <game>/mods/; requires BaseLib)"
