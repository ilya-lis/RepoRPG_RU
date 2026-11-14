#!/usr/bin/env bash
# End-to-end test of the add-on against a miniature RepoRPG (see tests/fake/RepoRPG.cs).
set -euo pipefail
cd "$(dirname "$0")/.."
RTK="${RTK:-$HOME/.cache/rtk}"
bash tools/build.sh >/dev/null || { bash tools/build.sh; exit 1; }
REF="$RTK/reference-assemblies/v4.6"
CORE=(-r:"$REF/mscorlib.dll" -r:"$REF/System.dll" -r:"$REF/System.Core.dll")
W="${TMPDIR:-/tmp}/reporpg_ru_test"
rm -rf "$W" && mkdir -p "$W/bin" "$W/fake"

# test doubles
"$RTK/csc.sh" "${CORE[@]}" -target:library -langversion:7.3 -out:"$W/bin/BepInEx.dll" src/Stubs/BepInEx.cs tests/BepInExTestOnly.cs
"$RTK/csc.sh" "${CORE[@]}" -target:library -langversion:7.3 -out:"$W/bin/UnityEngine.CoreModule.dll" tests/unity/CoreModule.cs
"$RTK/csc.sh" "${CORE[@]}" -target:library -langversion:7.3 -out:"$W/bin/UnityEngine.TextRenderingModule.dll" -r:"$W/bin/UnityEngine.CoreModule.dll" tests/unity/TextRenderingModule.cs
UNITY=(-r:"$W/bin/UnityEngine.CoreModule.dll" -r:"$W/bin/UnityEngine.TextRenderingModule.dll")
cp "$RTK/lib/Mono.Cecil.dll" "$W/bin/"
"$RTK/csc.sh" "${CORE[@]}" -target:exe -langversion:7.3 -out:"$W/bin/Host.exe" -r:"$W/bin/BepInEx.dll" -r:"$W/bin/Mono.Cecil.dll" tests/Host.cs
"$RTK/csc.sh" "${CORE[@]}" -target:library -langversion:7.3 -out:"$W/fake/RepoRPG.dll" -r:"$W/bin/BepInEx.dll" "${UNITY[@]}" \
    -resource:tests/fake/classes.json,RepoRPG.classes.json -resource:tests/fake/perks.json,RepoRPG.perks.json tests/fake/RepoRPG.cs
mkdir -p "$W/fake/v101"
sed 's/"1\.0\.100"/"1.0.101"/' tests/fake/RepoRPG.cs > "$W/fake/v101/RepoRPG.cs"
"$RTK/csc.sh" "${CORE[@]}" -target:library -langversion:7.3 -out:"$W/fake/v101/RepoRPG.dll" -r:"$W/bin/BepInEx.dll" "${UNITY[@]}" \
    -resource:tests/fake/classes.json,RepoRPG.classes.json -resource:tests/fake/perks.json,RepoRPG.perks.json "$W/fake/v101/RepoRPG.cs"

# the runtime of an older release: it has no Loc.MenuFont for the patched mod to call
mkdir -p "$W/old"
sed 's/public static object MenuFont(/public static object MenuFontOfLaterReleases(/' src/Runtime/Loc.cs > "$W/old/Loc.cs"
grep -q MenuFontOfLaterReleases "$W/old/Loc.cs"
"$RTK/csc.sh" "${CORE[@]}" -target:library -langversion:7.3 -nowarn:1591 -out:"$W/old/RepoRPG_RU.dll" "$W/old/Loc.cs" src/Runtime/Fonts.cs src/Shared/*.cs

export RTK W
exec python3 tests/check.py "$@"
