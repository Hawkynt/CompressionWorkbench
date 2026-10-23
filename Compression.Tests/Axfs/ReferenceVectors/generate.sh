#!/bin/bash
# Builds AXFS reference images with the legacy mkfs.axfs (jaredeh/axfs, mkfs.axfs-legacy,
# AXFS 2.2.0) and writes <name>.img.gz + <name>.manifest into $1.
set -e
out="${1:?usage: axfs-gen.sh <output-dir>}"; mkdir -p "$out"
tool=~/axfs/mkfs.axfs
d=$(mktemp -d); src=$d/src
mkdir -p $src/etc/init.d $src/bin $src/empty-dir $src/deep/a/b/c
printf 'hello axfs\n' > $src/etc/motd
seq 1 3000 > $src/etc/numbers.txt                      # ~13 KB, compressible, partial last page
head -c 4096 /dev/urandom > $src/bin/page.bin           # exactly one page, incompressible
head -c 9000 /dev/urandom > $src/bin/random.bin         # incompressible, partial tail
: > $src/empty
{ printf "ELF"; head -c 7000 /dev/urandom; } > $src/bin/fake-elf; chmod 0755 $src/bin/fake-elf
printf '#!/bin/sh\necho start\n' > $src/etc/init.d/rcS
cp $src/etc/numbers.txt $src/deep/a/b/c/copy.txt        # duplicate content
yes 'AXFS fills cblocks ' | head -c 20000 > $src/bin/repeat.txt
ln -s ../etc/motd $src/bin/motd-link
chmod 0755 $src/etc/init.d/rcS; chmod 0600 $src/bin/random.bin; chmod 0711 $src/deep
manifest() {
  (cd $src && find . -mindepth 1 | sort | while read -r p; do
     q=${p#./}
     if [ -L "$p" ]; then echo "L $(stat -c %a "$p") $q -> $(readlink "$p")";
     elif [ -d "$p" ]; then echo "D $(stat -c %a "$p") $q";
     else echo "F $(stat -c %a "$p") $(stat -c %s "$p") $(sha256sum < "$p" | cut -d' ' -f1) $q"; fi
   done)
}
$tool -s $src $d/axfs-compressed.img
$tool -s -a $src $d/axfs-xip.img
for n in axfs-compressed axfs-xip; do
  manifest > "$out/$n.manifest"
  gzip -9 -n -c $d/$n.img > "$out/$n.img.gz"
done
ls -la "$out"; cat "$out/axfs-compressed.manifest"
