#!/bin/bash
# Regenerates the DOS-order ProDOS reference disk. Not a build input; run on Ubuntu 24.04 (WSL2).
# $1: CiderPress II command-line tool (cp2 1.2.0, linux-x64 self-contained release).
set -euo pipefail
CP=${1:?path to cp2}
OUT=$(cd "$(dirname "$0")" && pwd)
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
mkdir -p "$W/src/SUBDIR"
printf 'HELLO FROM CIDERPRESS\r' > "$W/src/HELLO.TXT"
python3 -c "import sys; sys.stdout.buffer.write(bytes((i*31+1) & 255 for i in range(512)))" > "$W/src/SEEDLING"
python3 -c "import sys; sys.stdout.buffer.write(bytes((i*7+3) & 255 for i in range(5000)))" > "$W/src/SAPLING"
python3 -c "import sys; sys.stdout.buffer.write(bytes((i*13+5+(i>>9)) & 255 for i in range(70000)))" > "$W/src/SUBDIR/BIG"
cd "$W"
"$CP" create-disk-image cp2-prodos.do 140k prodos >/dev/null
( cd src && "$CP" add ../cp2-prodos.do HELLO.TXT SEEDLING SAPLING SUBDIR >/dev/null )
"$CP" catalog cp2-prodos.do > "$OUT/cp2-prodos.catalog.txt"
( cd src && find . -type f -print0 | sort -z | xargs -0 sha256sum | sed 's#  \./# #' ) > "$OUT/manifest.txt"
gzip -9 -n -c cp2-prodos.do > "$OUT/cp2-prodos.do.gz"
