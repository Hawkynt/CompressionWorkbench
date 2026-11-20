#!/bin/bash
# Makes the gBurner GBI fixtures with gBurner 5.9 (Free edition, official installer, run under wine
# in a prefix of its own: WINEPREFIX=$CWB_GBURNER_PREFIX, default ~/.cache/cwb/gburner-prefix).
# gBurner writes GBI only from its window (Tools > Convert Image), so this drives that dialog on a
# virtual X display (Xvfb, openbox, xdotool; the coordinates are those of a 1280x900 screen).
# The ISOs are built here from generated files; gbi-iso.sha256 records which ISO each GBI holds.
#   make-gbi.sh <output dir>
set -e
mkdir -p "$1"
out=$(realpath "$1")
export WINEPREFIX=${CWB_GBURNER_PREFIX:-$HOME/.cache/cwb/gburner-prefix} WINEDEBUG=-all DISPLAY=:78
W=$WINEPREFIX/drive_c/cwbgbi
rm -rf "$W" && mkdir -p "$W/src/sub" && cd "$W"
python3 - <<'EOF'
import random
r = random.Random(110)
words = 'alpha beta gamma delta image chunk lzma deflate table burner gbi'.split()
open('src/text.txt', 'w').write(' '.join(r.choice(words) for _ in range(30000)))
open('src/sub/noise.bin', 'wb').write(bytes(r.randrange(256) for _ in range(20000)))
code = bytearray()
while len(code) < 40000:
    code += bytes([0xE8]) + (0x4000 - len(code) - 5 + r.choice([0, 64])).to_bytes(4, 'little', signed=True) if r.random() < 0.3 else bytes([r.randrange(256)])
open('src/code.bin', 'wb').write(bytes(code))
open('more.bin', 'wb').write(bytes(r.randrange(256) for _ in range(200000)))
EOF
TZ=UTC find src more.bin -exec touch -d '2020-01-02 03:04:05' {} +
xorrisofs -quiet -no-pad -V GBIFIXTURE -J -r -o in.iso src
cp more.bin src/sub/
xorrisofs -quiet -no-pad -V GBIFIXTURE -J -r -o split.iso src

setsid -f Xvfb :78 -screen 0 1280x900x24 > /dev/null 2>&1
sleep 2
setsid -f openbox > /dev/null 2>&1
sleep 1
setsid -f wine "$WINEPREFIX/drive_c/Program Files/gBurner/gBurner.exe" > /dev/null 2>&1
sleep 15
x() { xdotool "$@"; sleep 1.5; }
field() { x mousemove "$1" "$2" click 1; xdotool key ctrl+a; xdotool type --delay 30 "$3"; sleep 0.5; }
props() { x mousemove 549 388 click 1; }               # Advanced Properties...
props_ok() { x mousemove 585 531 click 1; }
compression() { x mousemove 600 391 click 1; x mousemove 560 "$1" click 1; }  # 409 normal, 437 best
convert() { field 578 350 "$1"; x mousemove 327 474 click 1; sleep 10; }
done_ok() { x mousemove 642 483 click 1; }

x mousemove 94 632 click 1                             # Tools > Convert Image
field 578 158 'C:\cwbgbi\in.iso'
x mousemove 437 273 click 1; x mousemove 300 305 click 1   # output format: gBurner Images (.GBI)
convert 'C:\cwbgbi\gbi-normal.gbi'; done_ok
props; compression 437; props_ok
convert 'C:\cwbgbi\gbi-best.gbi'; done_ok
props; x mousemove 267 322 click 1; x mousemove 600 323 click 1   # enable password, Change password...
field 513 298 'Gbi-Secret_1'; field 513 337 'Gbi-Secret_1'; x mousemove 516 419 click 1
props_ok
convert 'C:\cwbgbi\gbi-password.gbi'; done_ok
props; x mousemove 267 322 click 1; x mousemove 610 483 click 1   # disable password: Yes
compression 409; x mousemove 267 220 click 1; field 590 250 '128K'; props_ok   # 128K is the smallest volume
field 578 158 'C:\cwbgbi\split.iso'
convert 'C:\cwbgbi\gbi-split.gbi'
x mousemove 597 509 click 1; sleep 8                   # confirm the volume names
done_ok

wineserver -k || true
pkill -f "Xvfb :78" || true
cp gbi-*.gbi "$out/"
{
  for f in gbi-normal gbi-best gbi-password; do echo "$(sha256sum < in.iso | cut -d' ' -f1)  $(stat -c %s in.iso)  $f.gbi"; done
  echo "$(sha256sum < split.iso | cut -d' ' -f1)  $(stat -c %s split.iso)  gbi-split.part01.gbi"
} > "$out/gbi-iso.sha256"
ls -la "$out"/gbi-*
cat "$out/gbi-iso.sha256"
cd / && rm -rf "$W"
