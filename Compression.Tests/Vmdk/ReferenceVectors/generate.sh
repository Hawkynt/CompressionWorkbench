#!/bin/bash
# Regenerates the VMDK reference images in this directory. Documentation, not a build
# input: the images are checked in so the reader tests need no external tools. Every
# image is written by qemu-img / qemu-io (qemu-utils); manifest.txt holds the SHA-256
# and length of what `qemu-img convert -O raw` makes of each, which is what the reader
# has to reproduce.
set -euo pipefail
OUT=$(cd "$(dirname "$0")" && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"

# An 8 MiB guest: text, incompressible runs and zero stretches, so grains are
# compressed, stored and absent.
python3 - <<'PY'
import random
r = random.Random(20261019)
words = [b"vmdk ", b"grain ", b"marker ", b"stream ", b"optimized\n"]
disk = bytearray(8 << 20)
for chunk in range(0, len(disk), 64 << 10):
    kind = (chunk >> 16) % 4
    if kind == 0:
        text = b"".join(r.choice(words) for _ in range(20000))[:64 << 10]
        disk[chunk:chunk + len(text)] = text
    elif kind == 1:
        disk[chunk:chunk + 4096] = bytes(r.getrandbits(8) for _ in range(4096))
    elif kind == 2:
        disk[chunk + 100:chunk + 200] = b"Z" * 100
open("guest.raw", "wb").write(disk)
PY

qemu-img convert -f raw -O vmdk -o subformat=streamOptimized guest.raw stream.vmdk
qemu-img convert -f raw -O vmdk -o subformat=monolithicSparse guest.raw sparse.vmdk
qemu-img convert -f raw -O vmdk -o subformat=monolithicFlat guest.raw flat.vmdk

# A delta disk over sparse.vmdk: two writes, one replacing part of a grain.
cp sparse.vmdk base.vmdk
qemu-img create -q -f vmdk -b base.vmdk -F vmdk delta.vmdk
qemu-io -f vmdk -c "write -P 0x5a 1M 64k" -c "write -P 0xa5 3200k 1k" delta.vmdk > /dev/null

# Zeroed grain-table entries: zero-writes over allocated grains.
qemu-img convert -f raw -O vmdk -o subformat=monolithicSparse,zeroed_grain=on guest.raw zeroed.vmdk
qemu-io -f vmdk -c "write -z 0 128k" -c "write -z 2M 64k" zeroed.vmdk > /dev/null

# Split sparse extents: a 3 GiB disk in 2 GiB pieces, written across the seam.
qemu-img create -q -f vmdk -o subformat=twoGbMaxExtentSparse split.vmdk 3G
qemu-io -f vmdk -c "write -P 0x11 0 64k" -c "write -P 0x22 2047M 2M" -c "write -P 0x33 2600M 64k" split.vmdk > /dev/null

: > "$OUT/manifest.txt"
for image in stream sparse flat delta zeroed split; do
  qemu-img check -q "$image.vmdk" 2>/dev/null || true
  qemu-img convert -O raw "$image.vmdk" "$image.out.raw"
  echo "$(sha256sum "$image.out.raw" | cut -d' ' -f1) $(stat -c %s "$image.out.raw") $image.vmdk" >> "$OUT/manifest.txt"
  rm "$image.out.raw"
done

rm -f "$OUT"/*.vmdk.gz
for f in *.vmdk; do gzip -9 -n -c "$f" > "$OUT/$f.gz"; done
ls -la "$OUT"
