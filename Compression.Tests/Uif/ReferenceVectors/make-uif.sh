#!/bin/bash
# Makes the UIF fixtures with MagicISO 5.5 build 0281 (the unregistered trial of the official
# Setup_MagicISO.exe, installed with `wine Setup_MagicISO.exe /s` into a prefix of its own:
# WINEPREFIX=$CWB_MAGICISO_PREFIX, default ~/.cache/cwb/magiciso-prefix).
# MagicISO writes UIF only from its window (Tools > Compress CD/DVD Image), so this drives that dialog
# on a virtual X display (Xvfb, openbox, xdotool; the coordinates are those of a 1280x1024 screen).
# The source images are built here from generated files; uif-sources.sha256 records each one.
#   make-uif.sh <output dir>
. "$(dirname "$(readlink -f "$0")")/../../Support/cwb-rmtree.sh"
set -e
mkdir -p "$1"
out=$(realpath "$1")
unset WAYLAND_DISPLAY
export WINEPREFIX=${CWB_MAGICISO_PREFIX:-$HOME/.cache/cwb/magiciso-prefix} WINEDEBUG=-all
W=$WINEPREFIX/drive_c/cwbuif
cwb_rmtree "$W"; mkdir -p "$W/src/sub" && cd "$W"
python3 - <<'EOF'
import random, math
r = random.Random(55)
words = 'magic iso uif sector block table zlib trailer image track audio'.split()
open('src/readme.txt', 'w').write('MagicISO UIF reference vector\n')
open('src/sub/words.txt', 'w').write(' '.join(r.choice(words) for _ in range(12000)))
open('src/noise.bin', 'wb').write(bytes(r.randrange(256) for _ in range(12000)))
EOF
TZ=UTC find src -exec touch -d '2020-01-02 03:04:05' {} +
SOURCE_DATE_EPOCH=1577934245 xorrisofs -quiet -no-pad -V UIFFIXTURE -J -r -o plain.iso src
python3 - <<'EOF'
import math
iso = open('plain.iso', 'rb').read()
def bcd(x): return ((x // 10) << 4) | (x % 10)
def msf(lba):
    lba += 150
    return bytes([bcd(lba // 4500), bcd(lba // 75 % 60), bcd(lba % 75)])
n = len(iso) // 2048
# Mode 1 raw sectors (sync, header, user data; EDC/ECC left zero) then a short audio track.
data = b''.join(b'\x00' + b'\xff' * 10 + b'\x00' + msf(i) + b'\x01' + iso[i * 2048:(i + 1) * 2048] + bytes(288) for i in range(n))
audio = b''.join(int(9000 * math.sin(i * 0.031)).to_bytes(2, 'little', signed=True) * 2 for i in range(40 * 588))
open('mixed.bin', 'wb').write(data + audio)
open('mixed.cue', 'w', newline='').write('FILE "mixed.bin" BINARY\r\n  TRACK 01 MODE1/2352\r\n    INDEX 01 00:00:00\r\n'
    '  TRACK 02 AUDIO\r\n    INDEX 01 %02d:%02d:%02d\r\n' % (n // 4500, n // 75 % 60, n % 75))
open('clone.img', 'wb').write(data)
open('clone.sub', 'wb').write(bytes(96 * n))
end = n + 150
toc = lambda e, point, pmin, psec, pframe, plba: (f'[Entry {e}]\r\nSession=1\r\nPoint={point}\r\nADR=0x01\r\nControl=0x04\r\nTrackNo=0\r\n'
    f'AMin=0\r\nASec=0\r\nAFrame=0\r\nALBA=-150\r\nZero=0\r\nPMin={pmin}\r\nPSec={psec}\r\nPFrame={pframe}\r\nPLBA={plba}\r\n')
open('clone.ccd', 'w', newline='').write('[CloneCD]\r\nVersion=3\r\n[Disc]\r\nTocEntries=4\r\nSessions=1\r\nDataTracksScrambled=0\r\n'
    'CDTextLength=0\r\n[Session 1]\r\nPreGapMode=1\r\nPreGapSubC=0\r\n' + toc(0, '0xa0', 1, 0, 0, 4350) + toc(1, '0xa1', 1, 0, 0, 4350)
    + toc(2, '0xa2', end // 4500, end // 75 % 60, end % 75, n) + toc(3, '0x01', 0, 2, 0, 0) + '[TRACK 1]\r\nMODE=1\r\nINDEX 1=0\r\n')
EOF
M="$WINEPREFIX/drive_c/Program Files (x86)/MagicISO"

n=79; while [ -e "/tmp/.X$n-lock" ] || [ -e "/tmp/.X11-unix/X$n" ]; do n=$((n + 1)); done   # a display nobody uses
export DISPLAY=:$n
Xvfb "$DISPLAY" -screen 0 1280x1024x24 > /dev/null 2>&1 &
xvfb=$!
sleep 2
wine "$M/miso.exe" nero.nrg -conv nrg mixed.cue -py > /dev/null 2>&1   # MagicISO's console tool writes the NRG
openbox > /dev/null 2>&1 &
wm=$!
sleep 1
setsid -f wine "$M/MagicISO.exe" > /dev/null 2>&1
sleep 15
x() { xdotool "$@"; sleep 1.5; }
field() { x mousemove "$1" "$2" click 1; xdotool key End shift+Home BackSpace; xdotool type --delay 30 "$3"; sleep 0.5; }
compress() {                                           # source, output, [password]
  x mousemove 348 273 click 1; x mousemove 430 370 click 1   # Tools > Compress CD/DVD Image
  field 620 425 "C:\\cwbuif\\$1"; field 615 490 "C:\\cwbuif\\$2"
  if [ -n "$3" ]; then x mousemove 465 530 click 1; field 550 576 "$3"; field 733 576 "$3"; fi
  x mousemove 702 635 click 1; sleep 10                # Compress
  x mousemove 790 635 click 1                          # Close
}
x mousemove 732 724 click 1                            # the trial nag: Try It!
compress plain.iso plain.uif
compress plain.iso password.uif 'Uif-Secret_31'
compress mixed.cue mixed.uif
compress mixed.cue mixed-password.uif 'Uif-Secret_31'
compress clone.ccd clonecd.uif
compress nero.nrg nero.uif

wineserver -k || true
kill "$wm" "$xvfb" || true
cp plain.uif password.uif mixed.uif mixed-password.uif clonecd.uif nero.uif "$out/"
for f in plain.iso mixed.bin mixed.cue clone.img clone.ccd clone.sub nero.nrg; do
  echo "$(sha256sum < $f | cut -d' ' -f1)  $(stat -c %s $f)  $f"
done > "$out/uif-sources.sha256"
ls -la "$out"/*.uif
cat "$out/uif-sources.sha256"
cd / && cwb_rmtree "$W"
