#!/bin/bash
# Extracts an archive (with its split volumes beside it) using ALZip's own console, ALZipCon.exe,
# under wine, and prints "sha256  path" for every file it wrote.
#
#   alzipcon-extract.sh --check
#   alzipcon-extract.sh <dir holding the volumes> <first volume name> [password]
#
# ALZip is ESTsoft's Windows freeware; install it once into a wine prefix of its own (it is untrusted
# software): ALZip1238.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=C:\ALZip with
# WINEPREFIX=$CWB_ALZIP_PREFIX (default ~/.cache/cwb/alzip-prefix).
. "$(dirname "$(readlink -f "$0")")/../Support/cwb-rmtree.sh"
export WINEPREFIX=${CWB_ALZIP_PREFIX:-$HOME/.cache/cwb/alzip-prefix} WINEDEBUG=-all
CON=$WINEPREFIX/drive_c/ALZip/ALZipCon.exe
if [ "$1" = "--check" ]; then
  [ -f "$CON" ] && command -v wine >/dev/null
  exit $?
fi
set -e
W=$(mktemp -d "$WINEPREFIX/drive_c/cwb-XXXXXX")
trap 'cwb_rmtree "$W"' EXIT
cp "$1"/* "$W/"
cd "$W"
PW=()
[ -n "$3" ] && PW=("-p$3")
timeout 300 wine "$CON" -x "${PW[@]}" "$2" out > /dev/null 2>&1 || { echo "ALZIPCON-FAILED rc=$?"; exit 1; }
cd out
find . -type f -print0 | sort -z | xargs -0 -r sha256sum
