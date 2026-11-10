#!/usr/bin/env bash
# Builds mod/lang/RepoRPG_RU.po from translation/ and checks it against the display-time translator.
set -euo pipefail
cd "$(dirname "$0")/.."
RTK="${RTK:-$HOME/.cache/rtk}"
W="${TMPDIR:-/tmp}/reporpg_ru_lang"
rm -rf "$W"; mkdir -p "$W/lang"
python3 tools/mkpo.py translation/units.json translation/ru mod/lang/RepoRPG_RU.po "$W/checks.tsv"
cp mod/lang/RepoRPG_RU.po "$W/lang/"
REF="$RTK/reference-assemblies/v4.6"
"$RTK/csc.sh" -nologo -r:"$REF/mscorlib.dll" -r:"$REF/System.dll" -r:"$REF/System.Core.dll" -target:exe -langversion:7.3 -nowarn:1591 \
    -out:"$W/LangCheck.exe" tests/LangCheck.cs src/Runtime/Loc.cs src/Shared/*.cs >/dev/null
"$RTK/run.sh" "$W/LangCheck.exe" "$W/checks.tsv" "$@"
