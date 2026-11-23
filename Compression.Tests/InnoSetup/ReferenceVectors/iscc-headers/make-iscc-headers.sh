#!/bin/bash
# Compiles ref.iss with each installed Inno Setup release under wine and prints the setup data id
# and the SHA-256 of both setup-0 blocks, as InnoSetupWriterVersionTests records them.
#
# usage: make-iscc-headers.sh <wine prefix> <release dir under drive_c> ...
#   e.g. make-iscc-headers.sh ~/.cache/cwb/inno-prefix IS605 IS612 IS622 ... IS711
# Each release was installed into the prefix from its official installer
# (https://github.com/jrsoftware/issrc/releases, tag is-<version>) with /VERYSILENT /DIR=C:\IS<ver>.
. "$(dirname "$(readlink -f "$0")")/../../../Support/cwb-rmtree.sh"
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
export WINEPREFIX="$1" WINEDEBUG=-all
shift
W=$(mktemp -d)
mkdir -p "$W/in/sub"
cd "$W"
printf 'hello inno\r\n' > in/hello.txt
head -c 3000 /dev/zero | tr '\0' 'x' > in/sub/data.bin
TZ=UTC touch -d '2020-01-02 03:04:04' in/hello.txt in/sub/data.bin
for d in "$@"; do
  # 6.0 and 6.1 ship WizModern*.bmp, later releases WizClassic*.bmp
  if [ -f "$WINEPREFIX/drive_c/$d/WizModernImage.bmp" ]; then img=WizModernImage.bmp; small=WizModernSmallImage.bmp
  else img=WizClassicImage.bmp; small=WizClassicSmallImage.bmp; fi
  sed -e "s/@IMG@/$img/" -e "s/@SMALL@/$small/" "$HERE/ref.iss" > ref.iss
  rm -rf out
  wine "C:\\$d\\ISCC.exe" /Q ref.iss < /dev/null > /dev/null 2>&1
  echo "$d $(python3 "$HERE/iscc-headers.py" out/ref.exe)"
done
cd /
cwb_rmtree "$W"
