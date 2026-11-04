#!/bin/sh
# Builds the Ex01 vectors with ewfacquire from libewf-experimental-20240506 (the first libewf
# release that writes EWF2; built from source, in WSL). Usage: sh mkfixtures.sh <workdir> <outdir>
# (documentation, not a build input). The images are stored gzipped; sources.sha256 holds the
# SHA-256 of the media each was acquired from.
set -e
work=$1; out=$2
export PATH=$HOME/libewf-exp/bin:$PATH
rm -rf "$work"; mkdir -p "$work"; cd "$work"
python3 -c "
import random
r = random.Random(17)
t = b'ex01 evidence chunk '
m = bytearray(192 * 1024)
for i in range(65536): m[i] = t[i % len(t)]
m[131072:] = bytes(r.getrandbits(8) for _ in range(65536))
open('small.raw', 'wb').write(m)
big = bytearray(2560 * 1024)
for i in range(0, len(big), 4096):
    if (i // 4096) % 3: big[i:i + 4096] = (b'segment %06d ' % (i // 4096)) * 256 + b'\0' * (4096 - 14 * 256)
open('big.raw', 'wb').write(big)
"
ewfacquire -u -q -f encase7-v2 -c deflate:best -t "$work/deflate" small.raw > /dev/null
ewfacquire -u -q -f encase7-v2 -c deflate:none -t "$work/stored" small.raw > /dev/null
ewfacquire -u -q -f encase7-v2 -c deflate:none -S 1MiB -t "$work/multi" big.raw > /dev/null
ls -l *.Ex*
printf '%s  deflate.Ex01\n%s  stored.Ex01\n%s  multi.Ex01\n' \
  "$(sha256sum < small.raw | cut -c1-64)" "$(sha256sum < small.raw | cut -c1-64)" "$(sha256sum < big.raw | cut -c1-64)" > sources.sha256
for f in *.Ex0*; do gzip -9 -n -c "$f" > "$out/$f.gz"; done
cp sources.sha256 "$out"
