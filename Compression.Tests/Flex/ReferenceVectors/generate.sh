#!/bin/bash
# Regenerates flexfloppy.dsk.gz + flexfloppy.manifest: flexfloppy (laurent-fr/flextools)
# formats a 35x10 disk and adds every file; the manifest is the sha256 of what the same
# tool extracts. Usage: generate.sh <output-dir> [path-to-flexfloppy]
set -e
out="${1:?usage: generate.sh <output-dir> [flexfloppy]}"; ff="${2:-$HOME/flex/flextools/flexfloppy/flexfloppy}"
mkdir -p "$out"; d=$(mktemp -d)
$ff --new --tracks 35 --sectors 10 --label CWBFIXTURE --number 42 --out $d/flexfloppy.dsk
mkdir $d/in; cd $d/in
printf 'HELLO FROM FLEXFLOPPY\r' > HELLO.TXT
seq 1 400 | tr '\n' '\r' > NUMBERS.TXT          # 1492 bytes: six sectors, the last one partial
head -c 252 /dev/urandom > EXACT.BIN           # exactly one sector
head -c 2000 /dev/urandom > RANDOM.BIN
for f in HELLO.TXT NUMBERS.TXT EXACT.BIN RANDOM.BIN; do $ff --in $d/flexfloppy.dsk --add $f; done
$ff --in $d/flexfloppy.dsk --cat
mkdir $d/x; $ff --in $d/flexfloppy.dsk --extract $d/x >/dev/null
(cd $d/x && sha256sum *) > "$out/flexfloppy.manifest"
gzip -9 -n -c $d/flexfloppy.dsk > "$out/flexfloppy.dsk.gz"
