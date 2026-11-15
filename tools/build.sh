#!/usr/bin/env bash
# Builds the two DLLs of the add-on into build/.
#   build/RepoRPG_RU.Patcher.dll  -> BepInEx/patchers   (references Mono.Cecil 0.10.4 + BepInEx, both provided by BepInEx)
#   build/RepoRPG_RU.dll          -> BepInEx/plugins    (references nothing but the core library)
# Both target .NET Framework 3.5-compatible C# (no LINQ, no netstandard facades) and reference mscorlib 4.0,
# which is what Unity's Mono binds for every BepInEx 5 plugin.
set -euo pipefail
cd "$(dirname "$0")/.."

RTK="${RTK:-$HOME/.cache/rtk}"
[ -x "$RTK/csc.sh" ] && [ -f "$RTK/lib/Mono.Cecil.dll" ] || bash tools/bootstrap.sh
REF="$RTK/reference-assemblies/v4.6"
CORE=(-r:"$REF/mscorlib.dll" -r:"$REF/System.dll" -r:"$REF/System.Core.dll")
COMMON=(-target:library -optimize+ -debug- -deterministic -langversion:7.3 -utf8output -nowarn:1591 -warnaserror+)

mkdir -p build/ref
"$RTK/csc.sh" "${CORE[@]}" "${COMMON[@]}" -out:build/ref/BepInEx.dll src/Stubs/BepInEx.cs

"$RTK/csc.sh" "${CORE[@]}" "${COMMON[@]}" -out:build/RepoRPG_RU.dll \
    src/Runtime/*.cs src/Shared/*.cs

"$RTK/csc.sh" "${CORE[@]}" "${COMMON[@]}" -out:build/RepoRPG_RU.Patcher.dll \
    -r:"$RTK/lib/Mono.Cecil.dll" -r:build/ref/BepInEx.dll \
    src/Patcher/*.cs src/Shared/*.cs

ls -l build/*.dll
