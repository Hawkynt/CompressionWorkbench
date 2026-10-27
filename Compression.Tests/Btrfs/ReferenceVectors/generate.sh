#!/bin/bash
# Regenerates the btrfs compressed-extent reference images in this directory.
# Documentation, not a build input: the images are checked in so the reader tests
# need no Linux tools. Needs mkfs.btrfs / btrfs (btrfs-progs) and guestfish
# (libguestfs-tools); no root — the Linux kernel inside the libguestfs appliance
# writes the files, so every extent is one the kernel's own btrfs driver produced.
set -euo pipefail
OUT=$(cd "$(dirname "$0")" && pwd)
SRC=$(mktemp -d)
WORK=$(mktemp -d)
trap 'rm -rf "$SRC" "$WORK"' EXIT

# Deterministic tree. Mostly compressible so the images stay small after gzip, with
# enough incompressible data to make the kernel fall back to plain extents too.
python3 - "$SRC" <<'PY'
import random, sys, os
src = sys.argv[1]
r = random.Random(20261019)
words = [b"alpha", b"beta", b"gamma", b"delta", b"btrfs", b"extent", b"compress", b"\n"]
open(os.path.join(src, "text.txt"), "wb").write(b" ".join(r.choice(words) for _ in range(50000)))
open(os.path.join(src, "small.txt"), "wb").write(b"tiny compressible inline payload " * 60)
mix = bytearray()
for i in range(8):
    mix += (b"A" * 16384) if i % 2 == 0 else bytes(r.getrandbits(8) for _ in range(16384))
open(os.path.join(src, "mixed.bin"), "wb").write(mix)
open(os.path.join(src, "island.bin"), "wb").write(b"island in the middle of a hole " * 2048)
PY

for method in zlib lzo zstd; do
  img="$WORK/$method.img"
  truncate -s 128M "$img"
  mkfs.btrfs -q -f -L "cwb-$method" -U 11111111-2222-3333-4444-555555555555 "$img"
  guestfish --rw -a "$img" <<EOF
run
mount-options compress-force=$method /dev/sda /
mkdir /d
upload $SRC/text.txt /d/text.txt
upload $SRC/small.txt /small.txt
upload $SRC/mixed.bin /mixed.bin
touch /sparse.bin
truncate-size /sparse.bin 1048576
sync
umount /
mount-options compress-force=$method /dev/sda /
pwrite /d/text.txt "OVERWRITTEN-IN-PLACE" 5000
fallocate64 /prealloc.bin 65536
sync
umount /
EOF
  # island.bin goes into the middle of sparse.bin: the hole before it has no extent
  # item at all (NO_HOLES is the mkfs default), the one after it ends the file.
  guestfish --rw -a "$img" <<EOF
run
mount-options compress-force=$method /dev/sda /
upload $SRC/island.bin /island.tmp
copy-file-to-file /island.tmp /sparse.bin destoffset:524288 size:63488 sparse:false
rm /island.tmp
truncate-size /sparse.bin 1048576
sync
umount /
EOF
  btrfs check --readonly "$img"
  # Evidence that the kernel really compressed: count file extents per method.
  btrfs inspect-internal dump-tree -t fs "$img" | grep -o 'compression [0-9]' | sort | uniq -c > "$WORK/$method.extents"
  # The bytes the image must read back, as the kernel sees them. The listing travels
  # into the appliance base64-encoded so no quoting layer can mangle it.
  inner=$(printf '%s\n' "cd /sysroot && find . -type f -printf '%P\n' | LC_ALL=C sort | while read -r f; do echo \"\$(sha256sum \"\$f\" | cut -d' ' -f1) \$(stat -c %s \"\$f\") \$f\"; done" | base64 -w0)
  guestfish --ro -a "$img" run : mount-ro /dev/sda / : debug sh "echo $inner | base64 -d | sh" > "$WORK/$method.manifest"
  gzip -9 -n -c "$img" > "$OUT/btrfs-$method.img.gz"
done

cmp "$WORK/zlib.manifest" "$WORK/lzo.manifest"
cmp "$WORK/zlib.manifest" "$WORK/zstd.manifest"
cp "$WORK/zstd.manifest" "$OUT/manifest.txt"
for method in zlib lzo zstd; do echo "== $method"; cat "$WORK/$method.extents"; done > "$OUT/extents.txt"
