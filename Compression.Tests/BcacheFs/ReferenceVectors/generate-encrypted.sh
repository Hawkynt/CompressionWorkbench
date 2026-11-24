#!/bin/bash
# Regenerates the encrypted bcachefs reference images in this directory.
# Documentation, not a build input: the images are checked in so the reader
# tests need no Linux tools.
#
#   BCACHEFS_NEW  a current bcachefs-tools build (used: v1.39.6)
#   PASSPHRASE    the passphrase of the wrapped-key images (used: Secr3t)
#
# The kernel images need the libguestfs appliance with a kernel that has the
# bcachefs driver (Ubuntu 24.04, 6.8). The appliance has no way to hand the kernel
# a passphrase, so the kernel writes into volumes formatted --no_passphrase: the
# master key is stored in the clear, but every b-tree node, journal set and extent
# is enciphered and MAC'd exactly as on a passphrase volume.
. "$(dirname "$(readlink -f "$0")")/../../Support/cwb-rmtree.sh"
set -euo pipefail
NEW=${BCACHEFS_NEW:?set BCACHEFS_NEW to a current bcachefs-tools binary}
DISTRO=${BCACHEFS_DISTRO:-bcachefs}
PASSPHRASE=${PASSPHRASE:-Secr3t}
OUT=$(cd "$(dirname "$0")" && pwd)
SRC=$(mktemp -d)
WORK=$(mktemp -d)
trap 'cwb_rmtree --sudo "$SRC" "$WORK"' EXIT

# A smaller tree than the compressed set (ciphertext does not compress): a binary
# pattern spanning several 64 KiB extents, an odd-sized text, a file smaller than
# a block, a sparse file with an island of data, and a link.
mkdir -p "$SRC/text" "$SRC/bin"
python3 - "$SRC" <<'PY'
import os, sys
root = sys.argv[1]
with open(os.path.join(root, "bin/pattern.bin"), "wb") as f:
    f.write(bytes((i * 31 + i // 977) & 255 for i in range(300000)))
with open(os.path.join(root, "text/odd-size.txt"), "w") as f:
    f.write("odd sized and compressible " * 700)
with open(os.path.join(root, "tiny.txt"), "w") as f:
    f.write("tiny\n")
PY
truncate -s 1048576 "$SRC/sparse.bin"
python3 -c "import sys; sys.stdout.write('island in the middle ' * 400)" | dd of="$SRC/sparse.bin" bs=1 seek=524288 conv=notrunc status=none
ln -s text/odd-size.txt "$SRC/link"

( cd "$SRC" && find . -type f -printf '%P\n' | sort | while read -r f; do
    echo "$(sha256sum "$f" | cut -d' ' -f1) $(stat -c %s "$f") $f"; done ) > "$OUT/manifest-encrypted.txt"

# The current tools wrapping a master key with a passphrase. Under WSL neither
# the distribution's 1.3 tools nor v1.39.6 get past writing the superblock of a
# passphrase volume (they cannot open it through the kernel keyring), with or
# without --source, so this image carries the tool-wrapped key and no files; the
# files of a passphrase volume are proven on our own volumes instead
# (BcacheFsEncryptedWriterTests: bcachefs fsck unlocks them from the passphrase).
truncate -s 32M "$WORK/key.img"
printf '%s\n%s\n%s\n' "$PASSPHRASE" "$PASSPHRASE" "$PASSPHRASE" | sudo "$NEW" format --force --encrypted --bucket_size=32k --source="$SRC" "$WORK/key.img" || true
sudo gzip -9 -n -c "$WORK/key.img" > "$OUT/bcachefs-tools-1.39-encrypted-key.img.gz"

# The 6.8 kernel's write path: plain extents with 80-bit MACs, and zstd extents
# with full 128-bit MACs (--wide_macs).
kernel() {
  local name=$1 options=$2; shift 2
  export LIBGUESTFS_BACKEND=direct
  truncate -s 32M "$WORK/$name.img"
  "$DISTRO" format -q --encrypted --no_passphrase --bucket=32k "$@" "$WORK/$name.img"
  tar -S -C "$SRC" -cf "$WORK/tree.tar" .
  guestfish -a "$WORK/$name.img" <<G
run
mount-vfs $options bcachefs /dev/sda /
tar-in $WORK/tree.tar /
umount /
G
  "$DISTRO" fsck -n "$WORK/$name.img"
  gzip -9 -n -c "$WORK/$name.img" > "$OUT/$name.img.gz"
}
kernel linux-6.8-encrypted noatime
kernel linux-6.8-encrypted-zstd-widemacs noatime,compression=zstd --wide_macs
