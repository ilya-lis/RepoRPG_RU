#!/usr/bin/env bash
# Builds mod/lang/RepoRPG_RU.po from translation/ and checks it against the display-time translator.
set -euo pipefail
cd "$(dirname "$0")/.."
RTK="${RTK:-$HOME/.cache/rtk}"
W="${TMPDIR:-/tmp}/reporpg_ru_lang"
rm -rf "$W"; mkdir -p "$W/lang"
python3 tools/mkpo.py translation/units.json translation/ru mod/lang/RepoRPG_RU.po "$W/checks.tsv"
cp mod/lang/RepoRPG_RU.po "$W/lang/"
# hand-written cases: texts that reach the screen untouched by the patcher (tests/drawn.tsv)
grep -v '^#' tests/drawn.tsv >> "$W/checks.tsv"
REF="$RTK/reference-assemblies/v4.6"
"$RTK/csc.sh" -nologo -r:"$REF/mscorlib.dll" -r:"$REF/System.dll" -r:"$REF/System.Core.dll" -target:exe -langversion:7.3 -nowarn:1591 \
    -out:"$W/LangCheck.exe" tests/LangCheck.cs src/Runtime/*.cs src/Shared/*.cs >/dev/null
"$RTK/csc.sh" -nologo -r:"$REF/mscorlib.dll" -r:"$REF/System.dll" -r:"$REF/System.Core.dll" -target:exe -langversion:7.3 -nowarn:1591 \
    -out:"$W/Shipped.exe" tests/Shipped.cs src/Patcher/Rules.cs src/Shared/*.cs >/dev/null
"$RTK/run.sh" "$W/Shipped.exe" mod "$W/lang"
status=0
"$RTK/run.sh" "$W/LangCheck.exe" "$W/checks.tsv" "$@" || status=$?
# the words of the game itself are written the way its official Russian localisation writes them (translation/official.tsv)
python3 tools/terms.py || status=$?
# The mod's own data files, rewritten the way the patcher rewrites them (tests/DataCheck.cs), then compared with the
# originals (tests/datacheck.py): same structure, same keys, tokens and numbers in place. Needs dump/ - the data files as
# the mod ships them (BepInEx/config/RepoRPG_RU/dump after a start with DumpStrings = true).
if ls dump/RepoRPG.*.json >/dev/null 2>&1; then
    "$RTK/csc.sh" -nologo -r:"$REF/mscorlib.dll" -r:"$REF/System.dll" -r:"$REF/System.Core.dll" -target:exe -langversion:7.3 -nowarn:1591 \
        -out:"$W/DataCheck.exe" tests/DataCheck.cs src/Patcher/Rules.cs src/Patcher/JsonText.cs src/Shared/*.cs >/dev/null
    [ -f "$W/DataCheck.runtimeconfig.json" ] || cp "$W/Shipped.runtimeconfig.json" "$W/DataCheck.runtimeconfig.json"
    "$RTK/run.sh" "$W/DataCheck.exe" mod "$W/lang" dump "$W/data" > "$W/datacheck.log" || { status=$?; cat "$W/datacheck.log"; }
    python3 tests/datacheck.py dump "$W/data" || status=$?
fi
exit $status
