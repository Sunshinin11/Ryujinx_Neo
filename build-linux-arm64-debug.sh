#!/usr/bin/env bash
set -Eeuo pipefail

RID="linux-arm64"
CONFIG="Debug"
OUT="$PWD/artifacts/neo-linux-arm64-debug"
PACKAGE="$PWD/artifacts/Ryujinx-Neo-linux-arm64-Debug.tar.gz"

MEM_FILE="src/Ryujinx.HLE/Loaders/Processes/ProcessLoaderHelper.cs"

echo "========================================"
echo " Ryujinx Neo Linux ARM64 Debug Build"
echo "========================================"

echo
echo "Arquitetura:"
uname -m

echo
echo "=== Corrigindo NEOV2-MEMLIMIT ==="

if [[ ! -f "$MEM_FILE" ]]; then
    echo "ERRO: arquivo não encontrado:"
    echo "$MEM_FILE"
    exit 1
fi

python3 <<'PY'
from pathlib import Path
import sys

path = Path("src/Ryujinx.HLE/Loaders/Processes/ProcessLoaderHelper.cs")
text = path.read_text()

old = "long memoryLimit = applicationRgSize + (long)context.MemorySlack;"
new = "long memoryLimit = applicationRgSize;"

if old not in text:
    print("ERRO: linha esperada não encontrada.")
    print()
    print("Linhas relacionadas encontradas:")

    for i, line in enumerate(text.splitlines(), 1):
        if (
            "memoryLimit" in line
            or "MemorySlack" in line
            or "NEOV2-MEMLIMIT" in line
            or "applicationRgSize" in line
        ):
            print(f"{i}: {line}")

    sys.exit(1)

text = text.replace(old, new, 1)

path.write_text(text)

print("PATCH APLICADO:")
print()
print("ANTES:")
print(old)
print()
print("DEPOIS:")
print(new)
print()
PY

echo "=== Verificando patch ==="

grep -n -E \
    "applicationRgSize|memoryLimit|MemorySlack|NEOV2-MEMLIMIT" \
    "$MEM_FILE" \
    | head -30

echo
echo "=== Selecionando projeto ==="

PROJECT=""

for p in \
    "src/Ryujinx/Ryujinx.csproj" \
    "src/Ryujinx.Ava/Ryujinx.Ava.csproj" \
    "Ryujinx/Ryujinx.csproj"
do
    if [[ -f "$p" ]]; then
        PROJECT="$p"
        break
    fi
done

if [[ -z "$PROJECT" ]]; then
    while IFS= read -r p; do
        if grep -Eq \
            '<OutputType>[[:space:]]*(Exe|WinExe)[[:space:]]*</OutputType>' \
            "$p" 2>/dev/null
        then
            PROJECT="$p"
            break
        fi
    done < <(
        find . \
            -maxdepth 4 \
            -type f \
            -iname '*Ryujinx*.csproj' \
            | sort
    )
fi

if [[ -z "$PROJECT" ]]; then
    echo "ERRO: projeto executável não encontrado."
    exit 1
fi

echo "Projeto:"
echo "$PROJECT"

echo
echo "=== Limpando build anterior ==="

rm -rf "$OUT"
rm -f "$PACKAGE"

mkdir -p "$OUT"

echo
echo "=== Restore ARM64 ==="

dotnet restore \
    "$PROJECT" \
    -r "$RID"

echo
echo "=== Publish ARM64 Debug ==="

dotnet publish \
    "$PROJECT" \
    -c "$CONFIG" \
    -r "$RID" \
    --self-contained true \
    -o "$OUT" \
    -p:DebugSymbols=true \
    -p:DebugType=portable \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -p:PublishReadyToRun=false

echo
echo "=== Verificando executável ==="

if [[ ! -f "$OUT/Ryujinx" ]]; then
    echo "ERRO: executável Ryujinx não foi gerado."
    exit 1
fi

chmod +x "$OUT/Ryujinx"

file "$OUT/Ryujinx"

echo
readelf -h "$OUT/Ryujinx" | grep -E 'Class|Machine' || true

echo
echo "=== Criando BUILD-INFO ==="

{
    echo "branch=$(git branch --show-current)"
    echo "commit=$(git rev-parse HEAD)"
    echo "architecture=$(uname -m)"
    echo "runtime=$RID"
    echo "configuration=$CONFIG"
    echo "project=$PROJECT"
    echo "neo_memlimit_fix=MemorySlack_removed"
    echo
    dotnet --info
} > "$OUT/BUILD-INFO.txt"

echo
echo "=== Criando pacote ==="

tar \
    -C "$PWD/artifacts" \
    -czf "$PACKAGE" \
    neo-linux-arm64-debug

echo
echo "========================================"
echo " BUILD FINALIZADA"
echo "========================================"

echo
echo "Executável:"
echo "$OUT/Ryujinx"

echo
echo "Pacote:"
echo "$PACKAGE"

ls -lh "$PACKAGE"
