#!/bin/bash
# Makes a split version-0x110 DAA set with PowerISO 9.5 for Windows (piso.exe under wine, prefix
# $CWB_POWERISO_PREFIX, default ~/.cache/cwb/poweriso-prefix) from 2.6 MB of generated data in
# 1 MiB volumes, writes the volumes into <output dir>, and prints the SHA-256 and size of the ISO
# PowerISO converts the set to.
#   piso-split-110.sh --check
#   piso-split-110.sh <output dir>
. "$(dirname "$(readlink -f "$0")")/../../Support/cwb-rmtree.sh"
export WINEPREFIX=${CWB_POWERISO_PREFIX:-$HOME/.cache/cwb/poweriso-prefix} WINEDEBUG=-all
P="$WINEPREFIX/drive_c/Program Files/PowerISO/piso.exe"
if [ "$1" = "--check" ]; then
  [ -f "$P" ] && command -v wine >/dev/null && command -v xvfb-run >/dev/null
  exit $?
fi
set -e
out=$(realpath "$1")
W=$(mktemp -d "$WINEPREFIX/drive_c/cwbsplit-XXXXXX")
trap 'cwb_rmtree "$W"' EXIT
cd "$W" && mkdir src
python3 -c "
import random
r = random.Random(7)
open('src/noise.bin', 'wb').write(bytes(r.randrange(256) for _ in range(2600000)))
open('src/text.txt', 'w').write(' '.join(r.choice(['split', 'volume', 'lzma', 'chunk']) for _ in range(50000)))
"
win=$(basename "$W")
xvfb-run -a wine "$P" create -o "C:\\$win\\set.daa" -add "C:\\$win\\src" / -method better -volsize 1M -y > /dev/null 2>&1
xvfb-run -a wine "$P" convert "C:\\$win\\set.part01.daa" -o "C:\\$win\\set.iso" -ot iso -y > /dev/null 2>&1
cp set.part*.daa "$out/"
echo "$(sha256sum < set.iso | cut -d' ' -f1) $(stat -c %s set.iso)"
