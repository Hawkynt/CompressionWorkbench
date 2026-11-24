#!/bin/sh
# Runs inside the s390x (big-endian) guest: makes two pools on /dev/vdb and /dev/vdc, fills them,
# snapshots, exports, and records what the guest's OpenZFS reads back.
O=/tmp/out
uname -m; uname -r
modprobe zfs || { echo "no zfs module"; exit 1; }
# file vdevs (no partitioning, no udev needed); copied onto the pool disks after export
truncate -s 64M /tmp/pool1 /tmp/pool2
zfs version
# a feature-flag pool with lz4 and system attributes
zpool create -f -o ashift=9 -O compression=lz4 -O atime=off -O mountpoint=/be bebe /tmp/pool1 || exit 1
# an old pool: version 28, file-system version 4 (znode bonus buffers, no system attributes)
zpool create -f -o ashift=9 -o version=28 -O version=4 -O compression=on -O atime=off -O mountpoint=/old beold /tmp/pool2 || exit 1
zfs create bebe/sub
for root in /be /old; do
  mkdir -p $root/docs/deep $root/empty-dir $root/many
  printf 'hello from a big-endian pool\n' > $root/hello.txt
  : > $root/empty
  yes "big-endian ZFS block pointer tree " | head -c 600000 > $root/big.txt
  head -c 100000 /dev/urandom > $root/docs/random.bin
  printf 'notes\n' > $root/docs/notes.txt
  head -c 5000 /dev/urandom > $root/docs/deep/leaf.dat
  i=0; while [ $i -lt 700 ]; do echo "entry $i" > $root/many/file-with-a-long-name-$i.txt; i=$((i+1)); done
  ln -s docs/notes.txt $root/link-to-notes
  ln -s /a/target/that/is/much/longer/than/the/bonus/buffer/would/hold/inline/for/sure/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa $root/long-link
  ln $root/docs/notes.txt $root/hard-notes
  chmod 640 $root/hello.txt; chown 1234:4321 $root/hello.txt
  dd if=/dev/zero of=$root/sparse.bin bs=1 count=1 seek=1048575 2>/dev/null
  touch -d @1200000000 $root/hello.txt
done
printf 'in a child dataset\n' > /be/sub/child.txt
zfs snapshot bebe@first
printf 'changed after the snapshot\n' > /be/hello.txt
sync; zpool sync
for p in bebe beold; do zpool get -H -o property,value version,feature@lz4_compress,feature@embedded_data $p; done
zdb -C bebe | head -20
manifest() {
  ( cd "$1" && find . -mindepth 1 -name .zfs -prune -o -print | sort | while IFS= read -r p; do
      q=${p#./}
      if [ -L "$p" ]; then echo "L $2$q -> $(readlink "$p")"
      elif [ -d "$p" ]; then echo "D $2$q"
      else echo "$(sha256sum < "$p" | cut -d' ' -f1) $(stat -c %s "$p") $2$q"; fi
    done )
}
manifest /be "" > $O/bebe.manifest
manifest /be/.zfs/snapshot/first ".zfs/snapshot/first/" >> $O/bebe.manifest
manifest /old "" > $O/beold.manifest
( cd /be && stat -c '%n %a %u %g %Y %h' hello.txt docs/notes.txt hard-notes sparse.bin ) > $O/bebe.stat
wc -l $O/*.manifest
zpool export bebe; zpool export beold
echo exported
dd if=/tmp/pool1 of=/dev/vdb bs=1M conv=fsync 2>&1 | tail -1
dd if=/tmp/pool2 of=/dev/vdc bs=1M conv=fsync 2>&1 | tail -1
