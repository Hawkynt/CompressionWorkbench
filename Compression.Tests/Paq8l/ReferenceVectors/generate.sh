#!/bin/bash
# Builds the paq8l reference archives: the generated inputs (make-inputs.py) compressed by paq8l
# itself (paq8l.cpp of paq8l.zip, built with g++ -O2 -DUNIX -DNOASM; byte-identical to the
# archive's paq8l.exe). Writes <name>.paq8l files and inputs.tar.gz into $1.
#   generate.sh <output dir> [paq8l binary, default ~/paq8l/paq8l-linux]
set -e
out=$(realpath "${1:?usage: generate.sh <output dir> [paq8l]}")
tool=$(realpath "${2:-$HOME/paq8l/paq8l-linux}")
here=$(dirname "$(realpath "$0")")
w=$(mktemp -d)
python3 -I "$here/make-inputs.py" "$w/in"
cd "$w/in"
# one archive per level over the mixed set, one per input type at level 5
for lv in 0 1 4 5; do
  "$tool" -$lv text.txt docs code.bin image.bmp photo.jpg one.txt empty.txt > /dev/null
  mv text.txt.paq8l "$out/mixed-l$lv.paq8l"
done
for f in fax.bin records.bin; do
  "$tool" -5 $f > /dev/null
  mv $f.paq8l "$out/${f%.*}-l5.paq8l"
done
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime=@0 -czf "$out/inputs.tar.gz" .
cd / && rm -rf "$w"
ls -la "$out"
