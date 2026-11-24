#!/bin/bash
# Makes Tux3 reference volumes with the tux3 user tools (linux-tux3 branch hirofumi-user).
#   cli.img      - tux3 mkfs, then files written into the root by `tux3 write` (one mount each),
#                  a symlink by `tux3 symlink` and a hard link by `tux3 link`
#   tree.img     - tux3 mkfs, then a directory tree with small files written through tux3fuse
#                  (tux3fuse of this branch aborts on writes of 4 KiB and more)
#   <v>-unified.img - each after `tux3 unify` folded its log into the trees
#   <v>.manifest    - what tux3fuse reads back from each image after a fresh mount
# Usage: tux3-gen.sh <out-dir> [tux3-user-dir]
set -u
out="${1:?out-dir}"; mkdir -p "$out"; out=$(readlink -f "$out")
U="${2:-$HOME/oracles/tux3/src/fs/tux3/user}"
d=$(mktemp -d); mnt=$d/mnt; mkdir -p $mnt
manifest() {
  ( cd "$1" && find . -mindepth 1 | sort | while IFS= read -r p; do
      q=${p#./}
      if [ -L "$p" ]; then echo "L $q -> $(readlink "$p")"
      elif [ -d "$p" ]; then echo "D $q"
      else echo "F $q $(stat -c '%s %a %h' "$p") $(sha256sum < "$p" | cut -d' ' -f1)"; fi
    done )
}
w() { $U/tux3 write $1 "$2" < "$3" > $d/w.log 2>&1 || { echo "write $2 rc=$?"; tail -3 $d/w.log; }; }

# --- cli.img
img=$out/cli.img; truncate -s 16M $img
$U/tux3 mkfs $img > /dev/null 2>&1
head -c 200000 /dev/urandom > $d/big; w $img big.bin $d/big
yes "tux3 data extent " | head -c 60000 > $d/text; w $img text.txt $d/text
printf 'hello from tux3\n' > $d/hello; w $img hello.txt $d/hello
w $img empty /dev/null
head -c 4096 /dev/urandom > $d/f4096; w $img f4096 $d/f4096
head -c 4097 /dev/urandom > $d/f4097; w $img f4097 $d/f4097
$U/tux3 fsck $img > $out/cli.fsck 2>&1; echo "fsck cli rc=$?"

# --- links.img: tux3 link and tux3 symlink (kept apart from cli.img)
img=$out/links.img; truncate -s 16M $img
$U/tux3 mkfs $img > /dev/null 2>&1
w $img hello.txt $d/hello
$U/tux3 link $img hello.txt hard-hello > $d/l.log 2>&1 || { echo "link rc=$?"; tail -3 $d/l.log; }
$U/tux3 symlink $img hello.txt sym-hello > $d/s.log 2>&1 || { echo "symlink rc=$?"; tail -3 $d/s.log; }
$U/tux3 fsck $img > $out/links.fsck 2>&1; echo "fsck links rc=$?"

# --- tree.img
img=$out/tree.img; truncate -s 16M $img
$U/tux3 mkfs $img > /dev/null 2>&1
($U/tux3fuse -f $img $mnt > $d/fuse.log 2>&1 &)
sleep 1
mkdir -p $mnt/docs/deep $mnt/empty-dir $mnt/many
printf 'hello from tux3\n' > $mnt/hello.txt
: > $mnt/empty
head -c 2000 /dev/urandom > $mnt/docs/random.bin
printf 'notes\n' > $mnt/docs/notes.txt
head -c 1500 /dev/urandom > $mnt/docs/deep/leaf.dat
for i in $(seq 1 150); do echo "entry $i" > $mnt/many/file-with-a-longer-name-$i.txt; done
mountpoint -q $mnt || echo "tux3fuse died while writing"
sync; fusermount3 -u $mnt 2>/dev/null; sleep 1
$U/tux3 fsck $img > $out/tree.fsck 2>&1; echo "fsck tree rc=$?"

for v in cli links tree; do
  cp $out/$v.img $out/$v-unified.img
  $U/tux3 unify $out/$v-unified.img > /dev/null 2>&1; echo "unify $v rc=$?"
  for i in $v $v-unified; do
    cp $out/$i.img $d/check.img
    ($U/tux3fuse -f $d/check.img $mnt > $d/fuse-$i.log 2>&1 &)
    sleep 1; manifest $mnt > $out/$i.manifest
    mountpoint -q $mnt || echo "tux3fuse died while reading $i"
    fusermount3 -u $mnt 2>/dev/null; sleep 1
    echo "$i: $(wc -l < $out/$i.manifest) entries"
  done
done
for f in hello f4096 f4097 text big; do echo "$f $(sha256sum < $d/$f | cut -d' ' -f1)"; done > $out/cli.sources
ls -la $out
