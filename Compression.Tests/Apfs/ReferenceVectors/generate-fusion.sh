#!/bin/bash
# Regenerates apfs-fusion-main.img.gz and apfs-fusion.manifest. Documentation, not a build input.
#
#   generate-fusion.sh <apfsprogs-dir> <apfs-fuse-build-dir>
#
# mkapfs -F (apfsprogs, https://github.com/linux-apfs/apfsprogs, b9c4cb40) formats a Fusion
# container over a main device and a tier-2 device; apfsck checks both; apfs-fuse -f reads
# the pair back. Only the main device is kept: everything a fresh container reaches lies on
# it, and the tier-2 device is all zeros apart from its superblock copy.
set -euo pipefail
OUT=$(cd "$(dirname "$0")" && pwd)
PROGS=$1; FUSE=$2
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
truncate -s 32M "$WORK/main.img"; truncate -s 64M "$WORK/tier2.img"
"$PROGS/mkapfs/mkapfs" -F "$WORK/tier2.img" -L fusion -U 11111111-2222-3333-4444-555555555555 "$WORK/main.img"
"$PROGS/apfsck/apfsck" -F "$WORK/tier2.img" "$WORK/main.img"
mkdir -p "$WORK/m"
"$FUSE/apfs-fuse" -o ro -f "$WORK/tier2.img" "$WORK/main.img" "$WORK/m"
sleep 1
{
  echo "# apfs-fuse $(git -C "$FUSE/.." rev-parse --short HEAD) -f tier2: the root of the fresh Fusion container"
  ( cd "$WORK/m/root" && find . -mindepth 1 -printf '%y %P\n' | LC_ALL=C sort )
} > "$OUT/apfs-fusion.manifest"
fusermount3 -u "$WORK/m"
gzip -9 -n -c "$WORK/main.img" > "$OUT/apfs-fusion-main.img.gz"
