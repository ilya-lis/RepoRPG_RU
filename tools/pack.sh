#!/usr/bin/env bash
# Builds the add-on and packs release/RepoRPG_RU-<version>.zip in the layout mod managers expect:
#   manifest.json, README.md, CHANGELOG.md, icon.png
#   patchers/RepoRPG_RU.Patcher.dll                 -> BepInEx/patchers/<package>/
#   plugins/RepoRPG_RU.dll, RepoRPG_RU.rules, lang/ -> BepInEx/plugins/<package>/
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(python3 -c "import json; print(json.load(open('mod/manifest.json', encoding='utf-8'))['version_number'])")
CODE=$(sed -n 's/.*public const string Version = "\([^"]*\)".*/\1/p' src/Shared/Settings.cs)
[ "$VERSION" = "$CODE" ] || { echo "manifest.json says $VERSION, src/Shared/Settings.cs says $CODE"; exit 1; }

bash tools/build.sh >/dev/null

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/patchers" "$STAGE/plugins/lang" release
cp mod/manifest.json mod/README.md mod/CHANGELOG.md mod/icon.png "$STAGE/"
cp build/RepoRPG_RU.Patcher.dll "$STAGE/patchers/"
cp build/RepoRPG_RU.dll mod/RepoRPG_RU.rules "$STAGE/plugins/"
cp mod/lang/*.po "$STAGE/plugins/lang/"

OUT="$PWD/release/RepoRPG_RU-$VERSION.zip"
rm -f "$OUT"
# fixed dates and order: the same sources give the same archive
find "$STAGE" -exec touch -d '2026-01-01 00:00:00 UTC' {} +
(cd "$STAGE" && find . -type f | LC_ALL=C sort | sed 's|^\./||' | TZ=UTC zip -q -X -9 "$OUT" -@)
unzip -l "$OUT"
