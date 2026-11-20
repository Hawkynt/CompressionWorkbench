#!/bin/bash
# Makes sc22_rr2.sqx and sc22_rr5.sqx: archives with an internal recovery record, written by the
# sqc.exe that ships with SpeedCommander 22.40 (32-bit trial, sc22_x32.exe from
# https://www.speedproject.com/download/, installed under wine in a prefix of its own:
# WINEPREFIX=$CWB_SC22_PREFIX, default ~/.cache/cwb/sc22-prefix). Squeez CommandLine 5.2 for
# Linux refuses -rr ("Access is denied"); this sqc.exe writes it.
#   make-internal-rr.sh <output dir>
set -e
mkdir -p "$1"
out=$(realpath "$1")
export WINEPREFIX=${CWB_SC22_PREFIX:-$HOME/.cache/cwb/sc22-prefix} WINEDEBUG=-all
SQ="$WINEPREFIX/drive_c/Program Files (x86)/SpeedProject/SpeedCommander 22/sqc.exe"
W=$WINEPREFIX/drive_c/rr
rm -rf "$W" && mkdir -p "$W" && cd "$W"
python3 -c "
import random
r = random.Random(40)
words = 'alpha beta gamma delta squeez recovery record'.split()
open('text.txt','w').write(' '.join(r.choice(words) for _ in range(20000)))
open('noise.bin','wb').write(bytes(r.getrandbits(8) for _ in range(30000)))
"
for level in 2 5; do
  wine "$SQ" a -ep -rr$level "C:\\rr\\rr$level.sqx" 'C:\rr\text.txt' 'C:\rr\noise.bin' < /dev/null > /dev/null 2>&1
  cp "rr$level.sqx" "$out/sc22_rr$level.sqx"
done
ls -la "$out"/sc22_rr*.sqx
