#!/bin/sh
# Runs as root inside the libguestfs appliance (see generate-crypt.sh, and
# generate.sh, which runs it too): OpenZFS 2.2.2 writes cwbcrypt, a pool with
# natively encrypted datasets in every suite family and key format.
#   gcm256        aes-256-gcm, passphrase "gcm-256-passphrase", lz4
#   gcm256/child  inherits gcm256's key (encryption root gcm256), gzip
#   ccm128        aes-128-ccm, raw key "0123456789abcdef0123456789abcdef", zstd
#   gcm192        aes-192-gcm, hex key 00112233..eeff (twice), off, recordsize 16K
# gcm256@s1 is taken before a file is removed and one added.
set -e
modprobe zfs
O=/sysroot/out
M=/sysroot/mnt
mkdir -p $O $M

text() { seq 1 "$2" | gawk '{print "line " $1 " the quick brown fox jumps over the lazy dog " ($1*7919)%10007}' > "$1"; }

manifest() {
  ( cd "$1" && find . -mindepth 1 -name .zfs -prune -o -print | sort | while IFS= read -r p; do
      q=${p#./}
      if [ -L "$p" ]; then echo "L $2$q -> $(readlink "$p")"
      elif [ -d "$p" ]; then echo "D $2$q"
      else echo "$(sha256sum < "$p" | cut -d' ' -f1) $(stat -c %s "$p") $2$q"; fi
    done )
}

fill() {
  d=$1
  text "$d/text.txt" 2000
  head -c 8000 /dev/urandom > "$d/rand.bin"
  head -c 70000 /dev/urandom > "$d/large.bin"
  printf 'tiny\n' > "$d/tiny.txt"
  : > "$d/empty.txt"
  truncate -s 1M "$d/sparse.bin"
  printf 'island' | dd of="$d/sparse.bin" bs=1 seek=700000 conv=notrunc 2>/dev/null
  mkdir -p "$d/dir/sub" "$d/many"
  printf 'deep\n' > "$d/dir/sub/deep.txt"
  # names past 50 bytes make the directory a fat ZAP
  for i in $(seq 1 40); do printf 'file %s\n' "$i" > "$d/many/$(printf 'n%.0s' $(seq 1 60))-$i.txt"; done
  ln -s text.txt "$d/link"
  ln -s "$(printf 'y%.0s' $(seq 1 200))/target" "$d/longlink"
  ln "$d/text.txt" "$d/hardlink.txt"
}

truncate -s 64M $O/cwbcrypt.img
zpool create -f -o ashift=9 -O mountpoint=$M/cwbcrypt cwbcrypt $O/cwbcrypt.img
printf '0123456789abcdef0123456789abcdef' > /tmp/raw.key
printf '00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff' > /tmp/hex.key
printf 'plain\n' > $M/cwbcrypt/plain.txt
echo 'gcm-256-passphrase' | zfs create -o encryption=aes-256-gcm -o keyformat=passphrase -o compression=lz4 cwbcrypt/gcm256
zfs create -o compression=gzip cwbcrypt/gcm256/child
zfs create -o encryption=aes-128-ccm -o keyformat=raw -o keylocation=file:///tmp/raw.key -o compression=zstd cwbcrypt/ccm128
zfs create -o encryption=aes-192-gcm -o keyformat=hex -o keylocation=file:///tmp/hex.key -o compression=off -o recordsize=16K cwbcrypt/gcm192
for d in gcm256 gcm256/child ccm128 gcm192; do fill $M/cwbcrypt/$d; done
zpool sync cwbcrypt
zfs snapshot cwbcrypt/gcm256@s1
rm $M/cwbcrypt/gcm256/rand.bin
text $M/cwbcrypt/gcm256/after.txt 500
zpool sync cwbcrypt
manifest $M/cwbcrypt "" > $O/cwbcrypt.manifest
manifest $M/cwbcrypt/gcm256/.zfs/snapshot/s1 "gcm256/.zfs/snapshot/s1/" >> $O/cwbcrypt.manifest
zpool scrub -w cwbcrypt
zpool status -v cwbcrypt >> $O/log.txt
zpool export cwbcrypt
echo CRYPT-DONE >> $O/log.txt
