#!/usr/bin/env bash
# Optional heavy check: run the builder over the Roslyn compiler itself (6 MB, 36k methods, netstandard2.0 like RepoRPG),
# inject two display hooks, then prove the rewritten compiler still compiles and reports errors exactly as before -
# with the runtime DLL present (hook reaches Loc.T) and absent (the guard switches itself off).
set -euo pipefail
cd "$(dirname "$0")/.."
RTK="${RTK:-$HOME/.cache/rtk}"
bash tools/build.sh >/dev/null
REF="$RTK/reference-assemblies/v4.6"
CORE=(-r:"$REF/mscorlib.dll" -r:"$REF/System.dll" -r:"$REF/System.Core.dll")
R="$RTK/roslyn-binaries/Microsoft.Net.Compilers/3.9.0"
W="${TMPDIR:-/tmp}/reporpg_ru_stress"
rm -rf "$W" && mkdir -p "$W/rt"

"$RTK/csc.sh" "${CORE[@]}" -target:exe -langversion:7.3 -optimize+ -out:"$W/Stress.exe" -r:"$RTK/lib/Mono.Cecil.dll" -r:build/ref/BepInEx.dll \
    -main:RepoRPG_RU.Stress tests/Stress.cs src/Patcher/*.cs src/Shared/*.cs
cp "$RTK/lib/Mono.Cecil.dll" build/ref/BepInEx.dll "$W/"

cp -r "$R" "$W/roslyn"
for f in Microsoft.CodeAnalysis.dll System.Collections.Immutable.dll System.Reflection.Metadata.dll; do
  "$RTK/run.sh" "$W/Stress.exe" "$R/$f" "$W/roslyn/$f" | head -2
done
"$RTK/run.sh" "$W/Stress.exe" "$R/Microsoft.CodeAnalysis.CSharp.dll" "$W/roslyn/Microsoft.CodeAnalysis.CSharp.dll" \
    'CSharpCommandLineParser::Parse(baseDirectory, sdkDirectory)' | head -2

cat > "$W/rt/Loc.cs" <<'CS'
namespace RepoRPG_RU {
    public static class Loc {
        public static string T(string s) {
            string f = System.Environment.GetEnvironmentVariable("LOC_TRACE");
            if (f != null) System.IO.File.AppendAllText(f, "T(" + s + ")\n");
            return s;
        }
    }
}
CS
"$RTK/csc.sh" "${CORE[@]}" -target:library -out:"$W/rt/RepoRPG_RU.dll" "$W/rt/Loc.cs"

cat > "$W/hello.cs" <<'CS'
using System; using System.Linq;
class P { static void Main() { var q = from x in new[]{3,1,2} orderby x select x*x; Console.WriteLine(string.Join(",", q) + " привет"); } }
CS
echo 'class P { static void Main() { int x = "s"; undefined(); } }' > "$W/bad.cs"

patched() {
  DOTNET_ROOT="$RTK/dotnetcore2/dotnetcore2/bin" DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 LD_LIBRARY_PATH="$RTK/ssl/lib" \
  "$RTK/dotnetcore2/dotnetcore2/bin/dotnet" "$W/roslyn/csc.exe" -nologo -noconfig -nostdlib+ "${CORE[@]}" -utf8output "$@"
}
fail() { echo "FAIL: $*"; exit 1; }

export LOC_TRACE="$W/trace.txt"
# runtime absent
patched -out:"$W/hello.exe" "$W/hello.cs" || fail "patched compiler does not compile (runtime absent)"
[ "$("$RTK/run.sh" "$W/hello.exe")" = "1,4,9 привет" ] || fail "wrong program output"
[ ! -e "$LOC_TRACE" ] || fail "Loc.T reached although the runtime is absent"
patched -out:"$W/bad.exe" "$W/bad.cs" > "$W/bad.patched.txt" || true
"$RTK/csc.sh" "${CORE[@]}" -utf8output -out:"$W/bad.exe" "$W/bad.cs" > "$W/bad.original.txt" || true
cmp -s "$W/bad.patched.txt" "$W/bad.original.txt" || fail "diagnostics differ from the original compiler"
grep -q CS0029 "$W/bad.original.txt" || fail "expected diagnostics missing"

# runtime present
cp "$W/rt/RepoRPG_RU.dll" "$W/roslyn/"
python3 - "$W/roslyn/csc.deps.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
t = list(d['targets'].values())[0]
t[list(t.keys())[0]]['runtime']['RepoRPG_RU.dll'] = {"assemblyVersion": "0.0.0.0", "fileVersion": "0.0.0.0"}
json.dump(d, open(sys.argv[1], 'w'), indent=1)
PY
rm -f "$W/hello.exe"
patched -out:"$W/hello.exe" "$W/hello.cs" || fail "patched compiler does not compile (runtime present)"
[ "$("$RTK/run.sh" "$W/hello.exe")" = "1,4,9 привет" ] || fail "wrong program output"
[ "$(wc -l < "$LOC_TRACE")" = "2" ] || fail "hooks did not reach Loc.T"
echo "stress: all good (rewritten compiler works, guard verified both ways)"
