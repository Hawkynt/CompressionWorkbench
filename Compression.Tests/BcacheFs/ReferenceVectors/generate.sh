#!/bin/bash
# Regenerates the bcachefs reference images in this directory. Documentation, not a
# build input: the images are checked in so the reader tests need no Linux tools.
# Needs sudo, to give files owners other than the invoking user.
#
#   BCACHEFS_DISTRO  bcachefs-tools as Debian/Ubuntu ship it (1.3.x); default: bcachefs
#   BCACHEFS_NEW     a current bcachefs-tools build (used: v1.39.6)
set -euo pipefail
DISTRO=${BCACHEFS_DISTRO:-bcachefs}
NEW=${BCACHEFS_NEW:?set BCACHEFS_NEW to a current bcachefs-tools binary}
OUT=$(cd "$(dirname "$0")" && pwd)
SRC=$(mktemp -d)
WORK=$(mktemp -d)
trap 'sudo rm -rf "$SRC" "$WORK"' EXIT

# The tree the populated image is made from. Contents are deterministic, so the
# manifest below is reproducible apart from the change times the kernel stamps.
mkdir -p "$SRC/sub/deeper" "$SRC/emptydir"
printf 'hello from bcachefs-tools\n' > "$SRC/hello.txt"
python3 -c "import sys; sys.stdout.buffer.write(bytes((i*31+i//977)&255 for i in range(300000)))" > "$SRC/sub/deeper/pattern.bin"
python3 -c "import sys; sys.stdout.buffer.write(bytes((i*7+3)&255 for i in range(4097)))" > "$SRC/sub/odd-size.bin"
truncate -s 1048576 "$SRC/sparse.bin"
printf 'island in the middle' | dd of="$SRC/sparse.bin" bs=1 seek=524288 conv=notrunc status=none
ln -s hello.txt "$SRC/link"
ln -s sub/deeper "$SRC/dirlink"

# Times, owners and modes worth reading back: sub-second precision, a time before
# the epoch, owners that are neither root nor the invoking user.
touch -m -d '2024-02-03 04:05:06.123456789 UTC' "$SRC/hello.txt"
touch -m -d '1969-07-20 20:17:40.5 UTC' "$SRC/sub/odd-size.bin"
chmod 0640 "$SRC/hello.txt"
chmod 0755 "$SRC/sub/odd-size.bin"
sudo chown 1234:4321 "$SRC/hello.txt"
sudo chown 0:100 "$SRC/sub/odd-size.bin"

{
  ( cd "$SRC" && find . -type f -printf '%P\n' | sort | while read -r f; do
      echo "$(sudo sha256sum "$f" | cut -d' ' -f1) $(stat -c %s "$f") $f"; done )
  ( cd "$SRC" && find . -type l -printf 'L %P -> %l\n' | sort )
  ( cd "$SRC" && find . -mindepth 1 -type d -printf 'D %P\n' | sort )
  # M <path> <octal permissions> <uid> <gid> <mtime ns> <ctime ns> (atime moves as the tool reads)
  ( cd "$SRC" && find . -type f -printf '%P\n' | sort | while read -r f; do
      python3 -c "import os,sys; s=os.stat(sys.argv[1]); print('M', sys.argv[1], oct(s.st_mode & 0o777)[2:], s.st_uid, s.st_gid, s.st_mtime_ns, s.st_ctime_ns)" "$f"; done )
} > "$OUT/manifest.txt"

# 1. What the distribution's tools make of an empty device: metadata version 1.3,
#    root directory and lost+found, cleanly shut down.
truncate -s 32M "$WORK/distro.img"
"$DISTRO" format -q --bucket=32k "$WORK/distro.img"
"$DISTRO" fsck -n "$WORK/distro.img"
gzip -9 -n -c "$WORK/distro.img" > "$OUT/bcachefs-tools-1.3-format.img.gz"

# 2. A volume current tools populated themselves, from the tree above. As root, so
#    it can read files that belong to other users.
truncate -s 32M "$WORK/source.img"
sudo "$NEW" format -q --force --bucket_size=32k --source="$SRC" "$WORK/source.img"
sudo "$NEW" fsck -n "$WORK/source.img"
sudo gzip -9 -n -c "$WORK/source.img" > "$OUT/bcachefs-tools-1.39-format-source.img.gz"
