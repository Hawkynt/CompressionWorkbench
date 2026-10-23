#!/bin/sh
# Regenerates the ZFS reference pools.  Needs libguestfs-tools and
# zfsutils-linux on the host (Ubuntu 24.04): the appliance boots the host's
# Ubuntu kernel, whose zfs.ko ships in linux-modules, and supermin copies the
# zfsutils-linux userland into it.  Run from this directory:
#
#   sudo sh -c "printf 'zfsutils-linux\nlibzfs4linux\nlibzpool5linux\nlibnvpair3linux\nlibuutil3linux\n' \
#     > /usr/lib/x86_64-linux-gnu/guestfs/supermin.d/packages-zfs"
#   sh generate.sh
set -e
here=$(cd "$(dirname "$0")" && pwd)
work=${WORK:-/tmp/cwbzfs}
mkdir -p "$work/cache" "$work/out"
export LIBGUESTFS_BACKEND=direct LIBGUESTFS_CACHEDIR="$work/cache" LIBGUESTFS_MEMSIZE=2048
rm -f "$work/scratch.img"
truncate -s 4G "$work/scratch.img"
guestfish -a "$work/scratch.img" <<EOF
run
mkfs ext4 /dev/sda
mount /dev/sda /
upload "$here/generate-inner.sh" /inner.sh
debug sh "sh /sysroot/inner.sh > /sysroot/log.txt 2>&1; echo rc=\$? >> /sysroot/log.txt"
download /log.txt $work/out/shell.txt
download /out/log.txt $work/out/log.txt
EOF
cat "$work/out/shell.txt"
grep -q DONE "$work/out/log.txt"
guestfish --ro -a "$work/scratch.img" -m /dev/sda <<EOF
copy-out /out "$work/"
EOF
for f in "$work"/out/*.img; do
  gzip -9 -n -c "$f" > "$here/$(basename "$f").gz"
done
cp "$work"/out/*.manifest "$here/"
