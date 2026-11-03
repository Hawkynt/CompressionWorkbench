#!/bin/bash
# Regenerates the SPIFFS reference images. Not a build input; run on Ubuntu 24.04 (WSL2).
# $1: directory holding mkspiffs builds named mkspiffs-esp8266, mkspiffs-generic and
#     mkspiffs-esp32 (github.com/igrr/mkspiffs f248296, SPIFFS f5e26c4; built with
#     `make dist BUILD_CONFIG_NAME=-arduino-esp8266 CPPFLAGS="-DSPIFFS_USE_MAGIC_LENGTH=0
#     -DSPIFFS_ALIGNED_OBJECT_INDEX_TABLES=1"`, `BUILD_CONFIG_NAME=-generic`, and
#     `BUILD_CONFIG_NAME=-arduino-esp32 CPPFLAGS="-DSPIFFS_OBJ_META_LEN=4"`)
#     and spiffsgen.py from ESP-IDF v5.2.2 (components/spiffs/spiffsgen.py).
set -euo pipefail
T=${1:?directory with the mkspiffs builds and spiffsgen.py}
OUT=$(cd "$(dirname "$0")" && pwd)
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
S=$W/src; mkdir -p "$S/dir/sub"
printf 'hello spiffs\n' > "$S/hello.txt"
: > "$S/empty.bin"
python3 -c "import sys; sys.stdout.buffer.write(bytes(i*7+3 & 255 for i in range(251)))" > "$S/one-page.bin"
python3 -c "import sys; sys.stdout.buffer.write(bytes(i*7+5 & 255 for i in range(252)))" > "$S/two-pages.bin"
# 40 000 bytes: more data pages than an index header page holds, so index pages follow.
python3 -c "import sys; sys.stdout.buffer.write(bytes((i*31+1+(i>>8)) & 255 for i in range(40000)))" > "$S/dir/big.bin"
printf 'nested\n' > "$S/dir/sub/x.txt"
printf 'thirty-one\n' > "$S/$(printf 'n%.0s' $(seq 1 30))"   # "/" + 30 bytes = 31, the longest name

"$T/mkspiffs-esp8266" -c "$S" -p 256 -b 8192 -s 131072 "$W/mkspiffs-esp8266.spiffs" >/dev/null
"$T/mkspiffs-generic" -c "$S" -p 256 -b 4096 -s 131072 "$W/mkspiffs-generic.spiffs" >/dev/null
"$T/mkspiffs-esp32"   -c "$S" -p 256 -b 4096 -s 131072 "$W/mkspiffs-esp32.spiffs" >/dev/null
python3 "$T/spiffsgen.py" 131072 "$S" "$W/spiffsgen.spiffs"

( cd "$S" && find . -type f -print0 | sort -z | xargs -0 sha256sum | sed 's#  \./# #' ) > "$OUT/manifest.txt"
for f in "$W"/*.spiffs; do gzip -9 -n -c "$f" > "$OUT/$(basename "$f").gz"; done
