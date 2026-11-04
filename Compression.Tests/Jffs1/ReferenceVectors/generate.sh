#!/bin/bash
# Builds JFFS (version 1) reference images with mkfs.jffs from mtd-utils as shipped in
# uClinux / DSLinux (user/mtd-utils/mkfs.jffs.c, GPL), compiled with gcc:
#   curl -sL https://raw.githubusercontent.com/ipwndev/DSLinux/master/user/mtd-utils/mkfs.jffs.c -o mkfs.jffs.c
#   gcc -O2 -w -o mkfs.jffs mkfs.jffs.c
# Usage: generate.sh <output-dir> [path-to-mkfs.jffs]
set -e
out="${1:?usage: generate.sh <output-dir> [mkfs.jffs]}"; tool="${2:-$HOME/jffs1/mkfs.jffs}"; mkdir -p "$out"
d=$(mktemp -d); src=$d/src
mkdir -p $src/etc/init.d $src/bin $src/empty-dir $src/deep/a/b
printf 'hello jffs\n' > $src/etc/motd
seq 1 20000 > $src/etc/numbers.txt                     # 108,894 bytes: four 32 KiB data nodes
head -c 3001 /dev/urandom > $src/bin/random.bin
: > $src/empty
printf '#!/bin/sh\necho start\n' > $src/etc/init.d/rcS
cp $src/etc/motd $src/deep/a/b/copy.txt
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
$tool -q -d $src -o $d/jffs1-le.img
$tool -q -a big -d $src -o $d/jffs1-be.img
for n in jffs1-le jffs1-be; do manifest > "$out/$n.manifest"; gzip -9 -n -c $d/$n.img > "$out/$n.img.gz"; done
ls -la "$out"
