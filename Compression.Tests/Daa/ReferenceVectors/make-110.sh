#!/bin/bash
# Makes the version-0x110 DAA fixtures with PowerISO 9.5 for Windows (piso.exe, under wine in a
# prefix of its own: WINEPREFIX=$CWB_POWERISO_PREFIX, default ~/.cache/cwb/poweriso-prefix) from
# generated inputs, and records PowerISO's own conversion of each back to ISO as SHA-256.
#   make-110.sh <output dir>
. "$(dirname "$(readlink -f "$0")")/../../Support/cwb-rmtree.sh"
set -e
out=$(realpath "$1")
export WINEPREFIX=${CWB_POWERISO_PREFIX:-$HOME/.cache/cwb/poweriso-prefix} WINEDEBUG=-all
P="$WINEPREFIX/drive_c/Program Files/PowerISO/piso.exe"
W=$WINEPREFIX/drive_c/cwb110
cwb_rmtree "$W"; mkdir -p "$W/src/sub" && cd "$W"
python3 - <<'EOF'
import random
r = random.Random(110)
words = 'alpha beta gamma delta image chunk lzma deflate table power iso'.split()
open('src/text.txt', 'w').write(' '.join(r.choice(words) for _ in range(30000)))
open('src/sub/noise.bin', 'wb').write(bytes(r.randrange(256) for _ in range(20000)))
# x86-like bytes with CALLs, so the LZMA chunks' x86 filter has work to do
code = bytearray()
while len(code) < 40000:
    code += bytes([0xE8]) + (0x4000 - len(code) - 5 + r.choice([0, 64])).to_bytes(4, 'little', signed=True) if r.random() < 0.3 else bytes([r.randrange(256)])
open('src/code.bin', 'wb').write(bytes(code))
EOF
piso() { xvfb-run -a wine "$P" "$@" -y > /dev/null 2>&1; }
piso create -o 'C:\cwb110\v110-better.daa' -add 'C:\cwb110\src' / -method better -file-datetime 01-02-2020-03:04:05 -vol-creation-datetime 01-02-2020-03:04:05
piso create -o 'C:\cwb110\v110-best.daa' -add 'C:\cwb110\src' / -method best -file-datetime 01-02-2020-03:04:05 -vol-creation-datetime 01-02-2020-03:04:05
piso create -o 'C:\cwb110\v110-password.daa' -add 'C:\cwb110\src' / -method better -setpassword secret42 -file-datetime 01-02-2020-03:04:05 -vol-creation-datetime 01-02-2020-03:04:05
: > "$out/v110-iso.sha256"
for f in v110-better v110-best v110-password; do
  piso convert "C:\\cwb110\\$f.daa" -o "C:\\cwb110\\$f.iso" -ot iso -password secret42
  cp $f.daa "$out/"
  echo "$(sha256sum < $f.iso | cut -d' ' -f1)  $(stat -c %s $f.iso)  $f.daa" >> "$out/v110-iso.sha256"
done
cat "$out/v110-iso.sha256"
cd / && cwb_rmtree "$W"
