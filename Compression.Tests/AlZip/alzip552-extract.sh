#!/bin/bash
# Extracts an ALZ archive with ALZip 5.52 itself (ALZip.exe -x, under wine on a virtual X display),
# and prints "sha256  path" for every file it wrote.
#
#   alzip552-extract.sh --check
#   alzip552-extract.sh <archive>
#
# ALZip 5.52 is ESTsoft's 2005 freeware, the build on the CHIP CD-ROM Romania 04/2005
# (archive.org item CMCD0405, Software/Freeware/Utilitare/alzip/ALZip.exe, SHA-256
# 06efdb543497afb5ff187359aacdd12ba595fe0153b949343522bedc4f5f61e0). Install it once into a wine
# prefix of its own (it is untrusted software):
#   ALZip.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=C:\ALZip
# with WINEPREFIX=$CWB_ALZIP552_PREFIX (default ~/.cache/cwb/alz2005-prefix).
export WINEPREFIX=${CWB_ALZIP552_PREFIX:-$HOME/.cache/cwb/alz2005-prefix} WINEDEBUG=-all
EXE=$WINEPREFIX/drive_c/ALZip/ALZip.exe
if [ "$1" = "--check" ]; then
  [ -f "$EXE" ] && command -v wine >/dev/null && command -v Xvfb >/dev/null
  exit $?
fi
set -e
DISP=:$((90 + RANDOM % 9))
Xvfb "$DISP" -screen 0 1024x768x24 >/dev/null 2>&1 &
XP=$!
W=$(mktemp -d "$WINEPREFIX/drive_c/cwb-XXXXXX")
trap 'WINEPREFIX=$WINEPREFIX wineserver -k 2>/dev/null; kill $XP 2>/dev/null; rm -rf "$W"' EXIT
sleep 1
cp "$1" "$W/in.alz"
N=$(basename "$W")
DISPLAY=$DISP timeout 120 wine 'C:\ALZip\ALZip.exe' -x "C:\\$N\\in.alz" "C:\\$N\\out" >/dev/null 2>&1 || true
[ -d "$W/out/in" ] || { echo "ALZIP-FAILED"; exit 1; }
cd "$W/out/in"
find . -type f -print0 | sort -z | xargs -0 -r sha256sum
