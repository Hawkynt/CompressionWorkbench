#!/bin/bash
# Regenerates apfs-clones.img.gz and apfs-clones.manifest. Documentation, not a build input.
#
#   generate-clones.sh <apfsprogs-dir> <linux-apfs-rw-dir> <apfs-fuse-build-dir>
#
# apfsprogs (https://github.com/linux-apfs/apfsprogs, b9c4cb40) formats the container and
# checks it; the linux-apfs-rw kernel module (https://github.com/linux-apfs/linux-apfs-rw,
# 6cf6de8a), built for the kernel the libguestfs appliance boots, writes the files, makes
# clones with `cp --reflink=always`, and a snapshot with apfs-snap; apfs-fuse
# (https://github.com/sgan81/apfs-fuse) reads the result back for the manifest.
set -euo pipefail
OUT=$(cd "$(dirname "$0")" && pwd)
PROGS=$1; KMOD=$2; FUSE=$3
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/pay"
cp "$KMOD/apfs.ko" "$PROGS/apfs-snap/apfs-snap" "$WORK/pay/"
python3 - "$WORK/pay" <<'PY'
import random, sys
r = random.Random(20261123)
d = sys.argv[1]
open(d + '/text.txt', 'wb').write(b''.join(r.choice([b'clone ', b'extent ', b'snapshot ', b'apfs\n']) for _ in range(40000)))
open(d + '/big.bin', 'wb').write(bytes(r.getrandbits(8) for _ in range(600000)))
PY
truncate -s 8M "$WORK/payload.img"; mkfs.ext2 -q -F -d "$WORK/pay" "$WORK/payload.img"
truncate -s 64M "$WORK/apfs.img"; "$PROGS/mkapfs/mkapfs" -L clones -U 11111111-2222-3333-4444-555555555555 "$WORK/apfs.img"

# Two clones made whole, a snapshot, then writes that make the original and one clone
# diverge (copy-on-write) and extend the other clone.
inner=$(cat <<'EOF' | base64 -w0
set -e
mkdir -p /src /w; mount -o ro /dev/sdb /src
modprobe libcrc32c
insmod /src/apfs.ko
mount -t apfs -o readwrite /dev/sda /w
cp /src/text.txt /w/text.txt
cp /src/big.bin /w/big.bin
mkdir /w/dir
cp --reflink=always /w/big.bin /w/dir/big-clone.bin
cp --reflink=always /w/text.txt /w/text-clone.txt
ln -s dir/big-clone.bin /w/link
sync
/src/apfs-snap /w before
printf 'CHANGED-AFTER-SNAPSHOT' | dd of=/w/big.bin bs=1 seek=4096 conv=notrunc status=none
printf 'CLONE-DIVERGED' | dd of=/w/dir/big-clone.bin bs=1 seek=300000 conv=notrunc status=none
echo appended >> /w/text-clone.txt
sync
umount /w
EOF
)
guestfish --rw -a "$WORK/apfs.img" -a "$WORK/payload.img" run : debug sh "echo $inner | base64 -d | sh"
"$PROGS/apfsck/apfsck" "$WORK/apfs.img"

mkdir -p "$WORK/m"
listing() {
  ( cd "$WORK/m/root" && find . -mindepth 1 -not -type d -printf '%P\n' | LC_ALL=C sort | while IFS= read -r p; do
      if [ -L "$p" ]; then echo "l $(readlink "$p") $1$p"
      elif [ -f "$p" ]; then echo "f $(sha256sum "$p" | cut -d' ' -f1) $1$p"
      else echo "o - $1$p"; fi
    done )
}
{
  echo "# apfs-fuse $(git -C "$FUSE/.." rev-parse --short HEAD); live volume, then snapshot 'before' (-o snap=<xid>)"
  "$FUSE/apfs-fuse" -o ro "$WORK/apfs.img" "$WORK/m"; sleep 1; listing ""; fusermount3 -u "$WORK/m"
  xid=$("$FUSE/apfsutil" "$WORK/apfs.img" | sed -n "s/^ *\([0-9]*\) : 'before'$/\1/p")
  echo "s $xid before"
  "$FUSE/apfs-fuse" -o ro,snap=$xid "$WORK/apfs.img" "$WORK/m"; sleep 1; listing ".snapshots/before/"; fusermount3 -u "$WORK/m"
} > "$OUT/apfs-clones.manifest"
gzip -9 -n -c "$WORK/apfs.img" > "$OUT/apfs-clones.img.gz"
