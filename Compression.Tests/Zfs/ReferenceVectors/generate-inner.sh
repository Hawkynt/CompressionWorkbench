#!/bin/sh
# Runs as root inside the libguestfs appliance (see generate.sh): the Ubuntu
# kernel's zfs.ko plus zfsutils-linux 2.2.2 create every pool, so OpenZFS wrote
# every byte of the vdev images.  /sysroot is an ext4 scratch disk.
set -e
modprobe zfs
O=/sysroot/out
M=/sysroot/mnt
mkdir -p $O $M
cd $O

text() { seq 1 "$2" | gawk '{print "line " $1 " the quick brown fox jumps over the lazy dog " ($1*7919)%10007}' > "$1"; }

# The full file set: compressible text, incompressible bytes, an embedded-sized
# file, an empty file, a sparse file with a hole, nested directories, a short
# and a long symlink, and a hard link.
fullset() {
  d=$1
  text "$d/text.txt" 3000
  head -c 16000 /dev/urandom > "$d/rand.bin"
  printf 'tiny file that should become an embedded block pointer\n' > "$d/tiny.txt"
  : > "$d/empty.txt"
  truncate -s 3M "$d/sparse.bin"
  printf 'island' | dd of="$d/sparse.bin" bs=1 seek=2097152 conv=notrunc 2>/dev/null
  mkdir -p "$d/dir/sub"
  printf 'deep\n' > "$d/dir/sub/deep.txt"
  ln -s text.txt "$d/link"
  ln -s "$(printf 'x%.0s' $(seq 1 200))/target" "$d/longlink"
  ln "$d/text.txt" "$d/hardlink.txt"
}

# The small file set, for datasets that vary one block-level property.
miniset() {
  d=$1
  text "$d/text.txt" 1000
  head -c 8192 /dev/urandom > "$d/rand.bin"
  printf 'tiny\n' > "$d/tiny.txt"
}

manifest() {
  ( cd "$1" && find . -mindepth 1 -name .zfs -prune -o -print | sort | while IFS= read -r p; do
      q=${p#./}
      if [ -L "$p" ]; then echo "L $2$q -> $(readlink "$p")"
      elif [ -d "$p" ]; then echo "D $2$q"
      else echo "$(sha256sum < "$p" | cut -d' ' -f1) $(stat -c %s "$p") $2$q"; fi
    done )
}

finish() {
  pool=$1
  zpool scrub -w "$pool"
  zpool status -v "$pool" >> $O/log.txt
}

# --- cwbmain: one ashift=12 file vdev, every compression and checksum ----------
truncate -s 128M $O/cwbmain.img
zpool create -f -o ashift=12 -O mountpoint=$M/cwbmain cwbmain $O/cwbmain.img
fullset $M/cwbmain
mkdir $M/cwbmain/many
for i in $(seq 1 600); do
  printf '%s\n' "$i" > "$M/cwbmain/many/a-rather-long-file-name-that-forces-a-fat-zap-directory-$i.txt"
done
for c in off lzjb zle gzip-1 gzip-9 lz4 zstd zstd-19 zstd-fast; do
  zfs create -o compression=$c cwbmain/c-$c
  fullset $M/cwbmain/c-$c
done
zfs create -o recordsize=4K -o compression=lz4 cwbmain/rs4k
miniset $M/cwbmain/rs4k
text $M/cwbmain/rs4k/big.txt 20000
# one record 16 MiB in: past 1024 level-0 pointers, so the file needs three levels
printf far | dd of=$M/cwbmain/rs4k/far.bin bs=4096 seek=4096 conv=notrunc 2>/dev/null
zfs create -o recordsize=1M -o compression=lz4 cwbmain/rs1m
miniset $M/cwbmain/rs1m
text $M/cwbmain/rs1m/big.txt 20000
zfs create -o dnodesize=auto -o xattr=sa cwbmain/largedn
fullset $M/cwbmain/largedn
zfs create -o copies=2 cwbmain/copies2
miniset $M/cwbmain/copies2
for k in fletcher2 sha256 sha512 skein edonr blake3; do
  zfs create -o checksum=$k cwbmain/k-$k
  miniset $M/cwbmain/k-$k
done
zfs create -o dedup=on cwbmain/dedup
miniset $M/cwbmain/dedup
cp $M/cwbmain/dedup/text.txt $M/cwbmain/dedup/copy.txt
zfs create -o version=4 cwbmain/zpl4
fullset $M/cwbmain/zpl4
zfs create cwbmain/nest
zfs create cwbmain/nest/child
miniset $M/cwbmain/nest/child
zfs snapshot cwbmain/c-lz4@before
text $M/cwbmain/c-lz4/text.txt 3500
rm $M/cwbmain/c-lz4/tiny.txt
sync
manifest $M/cwbmain "" > $O/cwbmain.manifest
manifest $M/cwbmain/c-lz4/.zfs/snapshot/before "c-lz4/.zfs/snapshot/before/" >> $O/cwbmain.manifest
zfs get -H -r -o name,property,value compression,checksum,recordsize,dnodesize,copies,dedup,version cwbmain >> $O/log.txt
finish cwbmain

# --- cwbv28: legacy pool version 28 (no feature flags), ashift=9 ----------------
truncate -s 64M $O/cwbv28.img
zpool create -f -o version=28 -o ashift=9 -O compression=on -O mountpoint=$M/cwbv28 cwbv28 $O/cwbv28.img
fullset $M/cwbv28
zfs create -o version=3 -o compression=gzip cwbv28/zpl3
fullset $M/cwbv28/zpl3
sync
manifest $M/cwbv28 "" > $O/cwbv28.manifest
finish cwbv28

# --- multi-device layouts ------------------------------------------------------
layout() {
  pool=$1; ashift=$2; comp=$3; shift 3
  vdevs=""
  for n in "$@"; do
    case $n in
      mirror|raidz1|raidz2) vdevs="$vdevs $n" ;;
      *) truncate -s 64M $O/$pool-$n.img; vdevs="$vdevs $O/$pool-$n.img" ;;
    esac
  done
  zpool create -f -o ashift=$ashift -O compression=$comp -O mountpoint=$M/$pool $pool $vdevs
  fullset $M/$pool
  text $M/$pool/big.txt 25000
  sync
  manifest $M/$pool "" > $O/$pool.manifest
  finish $pool
}
layout cwbmirror 9 lz4 mirror 0 1
layout cwbraidz1 9 off raidz1 0 1 2
layout cwbraidz2 12 lz4 raidz2 0 1 2 3
layout cwbstripe 9 lz4 0 1

# --- gang blocks: every allocation of 16 KiB or more is ganged -------------------
truncate -s 64M $O/cwbgang.img
zpool create -f -o ashift=9 -O compression=off -O mountpoint=$M/cwbgang cwbgang $O/cwbgang.img
echo 16384 > /sys/module/zfs/parameters/metaslab_force_ganging
echo 100 > /sys/module/zfs/parameters/metaslab_force_ganging_pct
fullset $M/cwbgang
text $M/cwbgang/big.txt 20000
sync
zpool sync cwbgang
echo 16777217 > /sys/module/zfs/parameters/metaslab_force_ganging
echo 3 > /sys/module/zfs/parameters/metaslab_force_ganging_pct
manifest $M/cwbgang "" > $O/cwbgang.manifest
finish cwbgang

# --- native encryption: the reader must refuse the encrypted dataset -------------
truncate -s 64M $O/cwbenc.img
zpool create -f -o ashift=9 -O mountpoint=$M/cwbenc cwbenc $O/cwbenc.img
printf 'plain\n' > $M/cwbenc/plain.txt
echo 'cwb-passphrase' | zfs create -o encryption=aes-256-gcm -o keyformat=passphrase -o keylocation=prompt cwbenc/secret
printf 'secret contents\n' > $M/cwbenc/secret/secret.txt
sync
zfs unmount cwbenc/secret
manifest $M/cwbenc "" > $O/cwbenc.manifest
finish cwbenc

for p in cwbmain cwbv28 cwbmirror cwbraidz1 cwbraidz2 cwbstripe cwbgang cwbenc; do
  zpool export $p
done
zfs version >> $O/log.txt
uname -r >> $O/log.txt
echo DONE >> $O/log.txt
