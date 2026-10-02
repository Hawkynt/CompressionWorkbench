#!/bin/bash
# Regenerates the bcachefs reference images in this directory. Documentation, not a
# build input: the images are checked in so the reader tests need no Linux tools.
#
#   BCACHEFS_DISTRO  bcachefs-tools as Debian/Ubuntu ship it (1.3.x); default: bcachefs
#   BCACHEFS_NEW     a current bcachefs-tools build (used: v1.39.6)
set -euo pipefail
DISTRO=${BCACHEFS_DISTRO:-bcachefs}
NEW=${BCACHEFS_NEW:?set BCACHEFS_NEW to a current bcachefs-tools binary}
OUT=$(cd "$(dirname "$0")" && pwd)
SRC=$(mktemp -d)
WORK=$(mktemp -d)
trap 'rm -rf "$SRC" "$WORK"' EXIT

# The tree the populated image is made from. Contents are deterministic, so the
# manifest below is reproducible.
mkdir -p "$SRC/sub/deeper" "$SRC/emptydir"
printf 'hello from bcachefs-tools\n' > "$SRC/hello.txt"
python3 -c "import sys; sys.stdout.buffer.write(bytes((i*31+i//977)&255 for i in range(300000)))" > "$SRC/sub/deeper/pattern.bin"
python3 -c "import sys; sys.stdout.buffer.write(bytes((i*7+3)&255 for i in range(4097)))" > "$SRC/sub/odd-size.bin"
truncate -s 1048576 "$SRC/sparse.bin"
printf 'island in the middle' | dd of="$SRC/sparse.bin" bs=1 seek=524288 conv=notrunc status=none
ln -s hello.txt "$SRC/link"
ln -s sub/deeper "$SRC/dirlink"

{
  ( cd "$SRC" && find . -type f -printf '%P\n' | sort | while read -r f; do
      echo "$(sha256sum "$f" | cut -d' ' -f1) $(stat -c %s "$f") $f"; done )
  ( cd "$SRC" && find . -type l -printf 'L %P -> %l\n' | sort )
  ( cd "$SRC" && find . -mindepth 1 -type d -printf 'D %P\n' | sort )
} > "$OUT/manifest.txt"

# 1. What the distribution's tools make of an empty device: metadata version 1.3,
#    root directory and lost+found, cleanly shut down.
truncate -s 32M "$WORK/distro.img"
"$DISTRO" format -q --bucket=32k "$WORK/distro.img"
"$DISTRO" fsck -n "$WORK/distro.img"
gzip -9 -n -c "$WORK/distro.img" > "$OUT/bcachefs-tools-1.3-format.img.gz"

# 2. A volume current tools populated themselves, from the tree above.
truncate -s 32M "$WORK/source.img"
"$NEW" format -q --force --bucket_size=32k --source="$SRC" "$WORK/source.img"
"$NEW" fsck -n "$WORK/source.img"
gzip -9 -n -c "$WORK/source.img" > "$OUT/bcachefs-tools-1.39-format-source.img.gz"
