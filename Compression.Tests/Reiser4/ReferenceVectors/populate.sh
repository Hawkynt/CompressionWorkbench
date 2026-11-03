# Guest script (run inside the Reiser4 kernel by generate.sh): fills a fresh volume through the
# kernel's own write path, unmounts it, and prints the manifest of what the kernel reads back.
mount -t reiser4 /dev/vda /mnt || { echo MOUNTFAIL; exit 1; }
cd /mnt
P=/payload
for f in $P/f_*; do cp $f ./$(basename $f); done
mkdir -p dir/sub/deeper emptydir
cp $P/f_4097 dir/sub/deeper/text.c
cp $P/f_4097 dir/foo.o
cp $P/f_100 dir/x.abc
cp $P/f_1 "dir/a-name-longer-than-twenty-three-characters.txt"
cp $P/f_1 "dir/$(printf 'L%.0s' $(seq 1 255))"
cp $P/f_100 "dir/another-long-name-for-the-hash.abc"
ln -s f_text sym_short
ln -s "$(printf 'target/%.0s' $(seq 1 60))end" sym_long
ln f_100 hard_100
ln f_100 dir/hard_100_b
mkfifo fifo
mknod chardev c 1 3
mknod blockdev b 7 0
dd if=$P/f_100 of=sparse bs=1 count=100 2>/dev/null
dd if=$P/f_4097 of=sparse bs=4096 seek=48 conv=notrunc 2>/dev/null
mkdir many
i=0; while [ $i -lt 300 ]; do echo "file $i" > many/m$i; i=$((i+1)); done
chown 1234:5678 f_100; chmod 0751 f_100
touch -d "2001-02-03 04:05:06" f_1
cd /
sync
umount /mnt || echo UMOUNTFAIL
mount -t reiser4 /dev/vda /mnt || { echo MOUNTFAIL; exit 1; }
cd /mnt
echo "===MANIFEST==="
find . | sort | while read p; do
  t=$(stat -c '%F|%a|%u|%g|%s|%h|%Y|%t:%T' "$p")
  if [ -L "$p" ]; then echo "$p|$t|$(readlink "$p")"
  elif [ -f "$p" ]; then echo "$p|$t|$(md5sum < "$p" | cut -d' ' -f1)"
  else echo "$p|$t|"; fi
done
echo "===END-MANIFEST==="
cd /
umount /mnt || echo UMOUNTFAIL
