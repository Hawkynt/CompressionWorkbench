#!/bin/sh
# Regenerates only cwbzvol (the pool of volumes) with the appliance generate.sh describes.
set -e
here=$(cd "$(dirname "$0")" && pwd)
work=${WORK:-/tmp/cwbzfs-zvol}
mkdir -p "$work/cache" "$work/out"
export LIBGUESTFS_BACKEND=direct LIBGUESTFS_CACHEDIR="$work/cache" LIBGUESTFS_MEMSIZE=2048
rm -f "$work/scratch.img"
truncate -s 512M "$work/scratch.img"
guestfish -a "$work/scratch.img" <<EOF
run
mkfs ext4 /dev/sda
mount /dev/sda /
upload "$here/generate-zvol-inner.sh" /zvol-inner.sh
debug sh "sh /sysroot/zvol-inner.sh > /sysroot/log.txt 2>&1; echo rc=\$? >> /sysroot/log.txt"
download /log.txt $work/out/shell.txt
download /out/log.txt $work/out/log.txt
download /out/cwbzvol.img $work/out/cwbzvol.img
download /out/cwbzvol.manifest $work/out/cwbzvol.manifest
EOF
cat "$work/out/shell.txt" "$work/out/log.txt"
grep -q ZVOL-DONE "$work/out/log.txt"
gzip -9 -n -c "$work/out/cwbzvol.img" > "$here/cwbzvol.img.gz"
cp "$work/out/cwbzvol.manifest" "$here/"
