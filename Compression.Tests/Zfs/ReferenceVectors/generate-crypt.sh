#!/bin/sh
# Regenerates only cwbcrypt (the natively encrypted reference pool) with the
# appliance generate.sh describes; generate.sh regenerates it with the rest.
set -e
here=$(cd "$(dirname "$0")" && pwd)
work=${WORK:-/tmp/cwbzfs-crypt}
mkdir -p "$work/cache" "$work/out"
export LIBGUESTFS_BACKEND=direct LIBGUESTFS_CACHEDIR="$work/cache" LIBGUESTFS_MEMSIZE=2048
rm -f "$work/scratch.img"
truncate -s 512M "$work/scratch.img"
guestfish -a "$work/scratch.img" <<EOF
run
mkfs ext4 /dev/sda
mount /dev/sda /
upload "$here/generate-crypt-inner.sh" /crypt-inner.sh
debug sh "sh /sysroot/crypt-inner.sh > /sysroot/log.txt 2>&1; echo rc=\$? >> /sysroot/log.txt"
download /log.txt $work/out/shell.txt
download /out/log.txt $work/out/log.txt
download /out/cwbcrypt.img $work/out/cwbcrypt.img
download /out/cwbcrypt.manifest $work/out/cwbcrypt.manifest
EOF
cat "$work/out/shell.txt"
grep -q CRYPT-DONE "$work/out/log.txt"
gzip -9 -n -c "$work/out/cwbcrypt.img" > "$here/cwbcrypt.img.gz"
cp "$work/out/cwbcrypt.manifest" "$here/"
