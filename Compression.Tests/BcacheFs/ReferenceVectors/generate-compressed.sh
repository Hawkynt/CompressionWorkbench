#!/bin/bash
# Regenerates the compressed / checksummed bcachefs reference images in this
# directory. Documentation, not a build input: the images are checked in so the
# reader tests need no Linux tools.
#
#   BCACHEFS_NEW     a current bcachefs-tools build (used: v1.39.6)
#   KERNEL_IMAGES=1  also let the distribution kernel (Ubuntu 24.04, 6.8) write a
#                    zstd volume through the libguestfs appliance
set -euo pipefail
NEW=${BCACHEFS_NEW:?set BCACHEFS_NEW to a current bcachefs-tools binary}
DISTRO=${BCACHEFS_DISTRO:-bcachefs}
OUT=$(cd "$(dirname "$0")" && pwd)
SRC=$(mktemp -d)
WORK=$(mktemp -d)
trap 'sudo rm -rf "$SRC" "$WORK"' EXIT

# A tree that compresses: text spanning many 64 KiB extents, a repeating binary
# pattern, a file of incompressible noise (stored plain beside the rest), a file
# that is not a whole number of blocks, a file smaller than a block, and a sparse
# file with an island of data. Deterministic, so the manifest is reproducible.
mkdir -p "$SRC/text" "$SRC/bin"
python3 - "$SRC" <<'PY'
import os, random, sys
root = sys.argv[1]
words = "bcachefs stores compressed extents behind a checksum entry naming their sizes".split()
rng = random.Random(1234)
with open(os.path.join(root, "text/lorem.txt"), "w") as f:
    for i in range(12000):
        f.write(" ".join(rng.choice(words) for _ in range(9)) + f" line {i}\n")
with open(os.path.join(root, "bin/pattern.bin"), "wb") as f:
    f.write(bytes((i * 31 + i // 977) & 255 for i in range(300000)))
with open(os.path.join(root, "bin/noise.bin"), "wb") as f:
    f.write(random.Random(99).randbytes(150000))
with open(os.path.join(root, "text/odd-size.txt"), "w") as f:
    f.write("odd sized and compressible " * 700)
with open(os.path.join(root, "tiny.txt"), "w") as f:
    f.write("tiny\n")
PY
truncate -s 1048576 "$SRC/sparse.bin"
python3 -c "import sys; sys.stdout.write('island in the middle ' * 400)" | dd of="$SRC/sparse.bin" bs=1 seek=524288 conv=notrunc status=none

( cd "$SRC" && find . -type f -printf '%P\n' | sort | while read -r f; do
    echo "$(sha256sum "$f" | cut -d' ' -f1) $(stat -c %s "$f") $f"; done ) > "$OUT/manifest-compressed.txt"

# One volume per compression type, each with a different data checksum so every
# checksum type is read back too.
make() {
  local name=$1; shift
  truncate -s 32M "$WORK/$name.img"
  sudo "$NEW" format -q --force --bucket_size=32k "$@" --source="$SRC" "$WORK/$name.img"
  sudo "$NEW" fsck -n "$WORK/$name.img"
  sudo gzip -9 -n -c "$WORK/$name.img" > "$OUT/$name.img.gz"
}
make bcachefs-tools-1.39-lz4-crc32c   --compression=lz4  --data_checksum=crc32c
make bcachefs-tools-1.39-gzip-crc64   --compression=gzip --data_checksum=crc64
make bcachefs-tools-1.39-zstd-xxhash  --compression=zstd --data_checksum=xxhash

if [ "${KERNEL_IMAGES:-0}" = 1 ]; then
  # The 6.8 kernel writes the same tree through its own write path, zstd-compressed.
  export LIBGUESTFS_BACKEND=direct
  truncate -s 32M "$WORK/kernel.img"
  "$DISTRO" format -q --bucket=32k "$WORK/kernel.img"
  tar -C "$SRC" -cf "$WORK/tree.tar" .
  guestfish -a "$WORK/kernel.img" <<G
run
mount-vfs compression=zstd bcachefs /dev/sda /
tar-in $WORK/tree.tar /
umount /
G
  "$DISTRO" fsck -n "$WORK/kernel.img"
  gzip -9 -n -c "$WORK/kernel.img" > "$OUT/linux-6.8-zstd.img.gz"
fi
