#!/bin/sh
# Runs as root inside the libguestfs appliance (see generate-zvol.sh): OpenZFS 2.2.2 writes
# cwbzvol, a pool whose datasets are volumes (zvols) rather than file systems.
#   vol         8 MiB, volblocksize 8K, lz4: text, random bytes, a zero run, more text
#   vol@s1      taken before the second text run was overwritten
#   sparse      32 MiB sparse volume (-s), volblocksize 16K, compression off: two islands
#   fs/vol2     a volume below a file system, 2 MiB, volblocksize 4K, gzip; fs holds one file
# The manifest holds the SHA-256 and size of every /dev/zvol device the kernel exposes.
set -e
modprobe zfs
O=/sysroot/out
M=/sysroot/mnt
mkdir -p $O $M
cd $O

text() { seq 1 "$1" | gawk '{print "line " $1 " the quick brown fox jumps over the lazy dog " ($1*7919)%10007}'; }

truncate -s 64M $O/cwbzvol.img
zpool create -f -o ashift=9 -O mountpoint=$M/cwbzvol cwbzvol $O/cwbzvol.img
zfs create -V 8M -o volblocksize=8K -o compression=lz4 -o snapdev=visible cwbzvol/vol
zfs create -s -V 32M -o volblocksize=16K -o compression=off cwbzvol/sparse
zfs create cwbzvol/fs
text 50 > $M/cwbzvol/fs/note.txt
zfs create -V 2M -o volblocksize=4K -o compression=gzip cwbzvol/fs/vol2
udevadm settle 2>/dev/null || sleep 2
for d in vol sparse fs/vol2; do i=0; while [ ! -e /dev/zvol/cwbzvol/$d ] && [ $i -lt 50 ]; do sleep 0.2; i=$((i+1)); done; done

V=/dev/zvol/cwbzvol/vol
text 20000 | dd of=$V bs=64K conv=notrunc 2>/dev/null
head -c 300000 /dev/urandom | dd of=$V bs=4096 seek=512 conv=notrunc 2>/dev/null
dd if=/dev/zero of=$V bs=64K seek=48 count=16 conv=notrunc 2>/dev/null
text 4000 | dd of=$V bs=4096 seek=1536 conv=notrunc 2>/dev/null
sync
zfs snapshot cwbzvol/vol@s1
text 500 | sed 's/fox/cat/' | dd of=$V bs=4096 seek=1600 conv=notrunc 2>/dev/null

S=/dev/zvol/cwbzvol/sparse
printf 'island one\n' | dd of=$S bs=1 seek=1048576 conv=notrunc 2>/dev/null
head -c 40000 /dev/urandom | dd of=$S bs=1000 seek=25000 conv=notrunc 2>/dev/null

V2=/dev/zvol/cwbzvol/fs/vol2
text 15000 | head -c 2097152 | dd of=$V2 bs=4096 conv=notrunc 2>/dev/null
sync
udevadm settle 2>/dev/null || sleep 2
i=0; while [ ! -e /dev/zvol/cwbzvol/vol@s1 ] && [ $i -lt 50 ]; do sleep 0.2; i=$((i+1)); done

: > $O/cwbzvol.manifest
for d in vol vol@s1 sparse fs/vol2; do
  dev=/dev/zvol/cwbzvol/$d
  echo "$(sha256sum < $dev | cut -d' ' -f1) $(blockdev --getsize64 $dev) $d" >> $O/cwbzvol.manifest
done
echo "$(sha256sum < $M/cwbzvol/fs/note.txt | cut -d' ' -f1) $(stat -c %s $M/cwbzvol/fs/note.txt) fs/note.txt" >> $O/cwbzvol.manifest
zfs get -H -o name,property,value volsize,volblocksize,compression cwbzvol/vol cwbzvol/sparse cwbzvol/fs/vol2 >> $O/log.txt
# Every dd through /dev/zvol is its own transaction group, and the copies they free would fill
# the image with stale blocks; a trim of the file vdev punches them out.
zpool trim -w cwbzvol
zpool scrub -w cwbzvol
zpool status -v cwbzvol >> $O/log.txt
zpool export cwbzvol
echo ZVOL-DONE >> $O/log.txt
