#!/bin/bash
# Regenerates the UFS reference volumes. Not a build input; run on Ubuntu 24.04 (WSL2).
# Needs: makefs (apt: makefs, the NetBSD tool, Debian 20190105-3), python3, gzip,
# and FreeBSD 8.2's newfs/fsck_ffs as packaged by Debian (ufsutils_8.2-3_amd64.deb
# from archive.debian.org, unpacked; pass its sbin directory as $1).
set -euo pipefail
UFSUTILS=${1:?path to the unpacked ufsutils sbin directory}
OUT=$(cd "$(dirname "$0")" && pwd)
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
S=$T/src
mkdir -p "$S/sub/deeper" "$S/emptydir"
printf 'hello ufs\n' > "$S/hello.txt"
: > "$S/empty.bin"
printf 'nested\n' > "$S/sub/deeper/n.txt"
# 2.6 MB patterned file: with 4 KiB blocks it needs the double-indirect tree in UFS2.
python3 -c "import sys; sys.stdout.buffer.write(bytes((i*31+1+(i>>12))&255 for i in range(2_700_000)))" > "$S/sub/big.bin"
printf 'linked\n' > "$S/hard1.txt"; ln "$S/hard1.txt" "$S/hard2.txt"
printf 'long name\n' > "$S/$(printf 'L%.0s' $(seq 1 255))"
printf 'utf8\n' > "$S/grüße.txt"
ln -s hello.txt "$S/fastlink"
ln -s "$(printf 'a%.0s' $(seq 1 59))" "$S/link59"
ln -s "$(printf 'b%.0s' $(seq 1 60))" "$S/link60"
ln -s "$(printf 'c%.0s' $(seq 1 119))" "$S/link119"
ln -s "$(printf 'd%.0s' $(seq 1 120))" "$S/link120"
find "$S" -exec touch -h -d '2024-02-29 12:34:56 UTC' {} +

for v in 1 2; do for bo in le be; do
  makefs -t ffs -o version=$v,bsize=4096,fsize=512 -B $bo -s 4m "$T/makefs-ufs$v-$bo.img" "$S" >/dev/null
done; done
"$UFSUTILS/fsck.ufs" -n "$T/makefs-ufs1-le.img" >/dev/null
"$UFSUTILS/fsck.ufs" -n "$T/makefs-ufs2-le.img" >/dev/null

# An empty UFS2 volume from FreeBSD's own newfs: superblock at SBLOCK_UFS2, four cylinder groups.
truncate -s 8M "$T/newfs-ufs2.img"
L=$(sudo losetup -f --show "$T/newfs-ufs2.img"); sudo "$UFSUTILS/mkfs.ufs" -O2 -L CWBUFS2 "$L" >/dev/null; sudo losetup -d "$L"
"$UFSUTILS/fsck.ufs" -n "$T/newfs-ufs2.img" >/dev/null

( cd "$S" && find . -type f -print0 | sort -z | xargs -0 sha256sum | sed 's#  \./# #'
  find . -type l -printf 'L %P -> %l\n' | sort
  find . -mindepth 1 -type d -printf 'D %P\n' | sort ) > "$OUT/manifest.txt"
for f in "$T"/*.img; do gzip -9 -n -c "$f" > "$OUT/$(basename "$f").gz"; done
