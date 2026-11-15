#!/usr/bin/env bash
# Bootstraps a self-contained C# toolchain in ~/.cache/rtk (nothing is installed system-wide).
#
# It only needs github.com, pypi.org and files.pythonhosted.org:
#   * .NET Core 3.1 runtime           <- PyPI wheel "dotnetcore2"
#   * OpenSSL 1.1 (needed by 3.1)     <- PyPI wheel "psycopg2-binary" (bundled libssl/libcrypto)
#   * Roslyn 3.9.0 (csc.exe, C# 9)    <- github.com/mono/roslyn-binaries
#   * .NET Framework 4.6 ref asms     <- github.com/mono/reference-assemblies
#   * Mono.Cecil 0.10.4 (from source) <- github.com/jbevain/cecil   (the version bundled with BepInEx 5.4.23)
#   * BepInEx 5.4.23.5 sources        <- github.com/BepInEx/BepInEx (only read, to check the patcher contract)
#
# Result:  ~/.cache/rtk/csc.sh   - C# compiler wrapper
#          ~/.cache/rtk/run.sh   - runs a managed .exe on .NET Core 3.1
#          ~/.cache/rtk/lib/Mono.Cecil.dll
#          ~/.cache/rtk/reference-assemblies/v4.6/*.dll
set -euo pipefail

RTK="${RTK:-$HOME/.cache/rtk}"
mkdir -p "$RTK"
cd "$RTK"

log() { printf '\033[1;34m[bootstrap]\033[0m %s\n' "$*"; }

# ---------------------------------------------------------------- python venv (pip download + helpers)
if [ ! -x venv/bin/pip ]; then
  log "python venv"
  python3 -m venv venv
fi
if ! venv/bin/python -c 'import dnfile' 2>/dev/null; then
  venv/bin/pip install --quiet dnfile
fi

# ---------------------------------------------------------------- .NET Core 3.1 runtime
if [ ! -x dotnetcore2/dotnetcore2/bin/dotnet ]; then
  log ".NET Core 3.1 runtime (dotnetcore2 wheel)"
  mkdir -p wheels
  venv/bin/pip download dotnetcore2==3.1.23 --no-deps -d wheels \
      --platform manylinux1_x86_64 --only-binary=:all: --quiet
  rm -rf dotnetcore2 && mkdir dotnetcore2
  unzip -q -o wheels/dotnetcore2-3.1.23-*.whl -d dotnetcore2
  chmod +x dotnetcore2/dotnetcore2/bin/dotnet
fi
DOTNET_ROOT="$RTK/dotnetcore2/dotnetcore2/bin"

# ---------------------------------------------------------------- OpenSSL 1.1 for the 3.1 runtime
if [ ! -e ssl/lib/libssl.so.1.1 ]; then
  log "OpenSSL 1.1 (from the psycopg2-binary wheel)"
  mkdir -p ssl/lib
  venv/bin/pip download psycopg2-binary==2.8.6 --no-deps -d ssl \
      --platform manylinux1_x86_64 --python-version 38 --only-binary=:all: --quiet
  unzip -q -o ssl/psycopg2_binary-2.8.6-*.whl 'psycopg2_binary.libs/libssl-*' \
      'psycopg2_binary.libs/libcrypto-*' 'psycopg2_binary.libs/libz-*' -d ssl
  cp ssl/psycopg2_binary.libs/* ssl/lib/
  ( cd ssl/lib
    ln -sf "$(ls libssl-*.so.1.1*    | head -1)" libssl.so.1.1
    ln -sf "$(ls libcrypto-*.so.1.1* | head -1)" libcrypto.so.1.1 )
fi

# ---------------------------------------------------------------- Roslyn
ROSLYN="$RTK/roslyn-binaries/Microsoft.Net.Compilers/3.9.0"
if [ ! -f "$ROSLYN/csc.exe" ]; then
  log "Roslyn 3.9.0"
  rm -rf roslyn-binaries
  git clone --quiet --filter=blob:none --sparse --depth 1 https://github.com/mono/roslyn-binaries
  git -C roslyn-binaries sparse-checkout set Microsoft.Net.Compilers/3.9.0
fi

cat > csc.runtimeconfig.json <<'EOF'
{
  "runtimeOptions": {
    "tfm": "netcoreapp3.1",
    "framework": { "name": "Microsoft.NETCore.App", "version": "3.1.0" },
    "configProperties": { "System.Globalization.Invariant": true }
  }
}
EOF
cp csc.runtimeconfig.json "$ROSLYN/csc.runtimeconfig.json"

# csc.exe is a .NET Framework build. The 3.1 shared framework ships older System.Collections.Immutable /
# System.Reflection.Metadata, so a deps.json is needed to make the host prefer Roslyn's own copies.
if [ ! -f "$ROSLYN/csc.deps.json" ]; then
  venv/bin/python - "$ROSLYN" <<'EOF' 2>/dev/null
import dnfile, json, os, sys
root = sys.argv[1]
keep = ['Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll', 'System.Collections.Immutable.dll',
        'System.Reflection.Metadata.dll', 'System.Runtime.CompilerServices.Unsafe.dll']
runtime = {}
for name in keep:
    pe = dnfile.dnPE(os.path.join(root, name))
    a = pe.net.mdtables.Assembly.rows[0]
    av = "%d.%d.%d.%d" % (a.MajorVersion, a.MinorVersion, a.BuildNumber, a.RevisionNumber)
    try:
        ms = pe.VS_FIXEDFILEINFO[0]
        fv = "%d.%d.%d.%d" % (ms.FileVersionMS >> 16, ms.FileVersionMS & 0xffff, ms.FileVersionLS >> 16, ms.FileVersionLS & 0xffff)
    except Exception:
        fv = av
    runtime[name] = {"assemblyVersion": av, "fileVersion": fv}
deps = {
    "runtimeTarget": {"name": ".NETCoreApp,Version=v3.1", "signature": ""},
    "compilationOptions": {},
    "targets": {".NETCoreApp,Version=v3.1": {"csc/3.9.0": {"runtime": runtime}}},
    "libraries": {"csc/3.9.0": {"type": "project", "serviceable": False, "sha512": ""}},
}
json.dump(deps, open(os.path.join(root, 'csc.deps.json'), 'w'), indent=1)
EOF
fi

# ---------------------------------------------------------------- reference assemblies
if [ ! -f reference-assemblies/v4.6/mscorlib.dll ]; then
  log ".NET Framework 4.6 reference assemblies"
  rm -rf reference-assemblies
  git clone --quiet --filter=blob:none --sparse --depth 1 https://github.com/mono/reference-assemblies
  git -C reference-assemblies sparse-checkout set v4.6
fi

# ---------------------------------------------------------------- wrappers
cat > csc.sh <<EOF
#!/bin/sh
# Roslyn 3.9.0 csc on the .NET Core 3.1 runtime
export DOTNET_ROOT=$DOTNET_ROOT
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export LD_LIBRARY_PATH=$RTK/ssl/lib\${LD_LIBRARY_PATH:+:\$LD_LIBRARY_PATH}
exec \$DOTNET_ROOT/dotnet $ROSLYN/csc.exe -nologo -noconfig -nostdlib+ "\$@"
EOF
cat > run.sh <<EOF
#!/bin/sh
# runs a managed exe on the .NET Core 3.1 runtime (a <name>.runtimeconfig.json is created next to it on demand)
export DOTNET_ROOT=$DOTNET_ROOT
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export LD_LIBRARY_PATH=$RTK/ssl/lib\${LD_LIBRARY_PATH:+:\$LD_LIBRARY_PATH}
exe="\$1"; shift
cfg="\${exe%.*}.runtimeconfig.json"
[ -f "\$cfg" ] || cp $RTK/csc.runtimeconfig.json "\$cfg"
exec \$DOTNET_ROOT/dotnet "\$exe" "\$@"
EOF
chmod +x csc.sh run.sh

# ---------------------------------------------------------------- sources: Cecil 0.10.4, BepInEx 5.4.23.5
mkdir -p src lib
if [ ! -d src/cecil/Mono.Cecil ]; then
  log "Mono.Cecil 0.10.4 sources"
  rm -rf src/cecil
  git clone --quiet --depth 1 --branch 0.10.4 https://github.com/jbevain/cecil src/cecil
fi
if [ ! -d src/bepinex/BepInEx ]; then
  log "BepInEx 5.4.23.5 sources (reference only)"
  rm -rf src/bepinex
  git clone --quiet --depth 1 --branch v5.4.23.5 https://github.com/BepInEx/BepInEx src/bepinex
fi

REF="$RTK/reference-assemblies/v4.6"
if [ ! -f lib/Mono.Cecil.dll ]; then
  log "building Mono.Cecil 0.10.4 (signed with the upstream cecil.snk => identical assembly identity)"
  ( cd src/cecil
    "$RTK/csc.sh" -target:library -out:"$RTK/lib/Mono.Cecil.dll" -optimize+ -debug- \
      "-define:NET_4_0;TRACE" -keyfile:cecil.snk -langversion:7.3 -nowarn:1591,618,649,414,169 \
      -r:"$REF/mscorlib.dll" -r:"$REF/System.dll" -r:"$REF/System.Core.dll" \
      ProjectInfo.cs Mono/*.cs Mono.Cecil/*.cs Mono.Cecil.Cil/*.cs Mono.Cecil.Metadata/*.cs \
      Mono.Cecil.PE/*.cs Mono.Collections.Generic/*.cs Mono.Security.Cryptography/*.cs )
fi

log "ready: $RTK"
