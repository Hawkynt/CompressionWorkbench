# Guest script: mounts /dev/vda and prints one line per object the kernel resolves, reading every
# regular file whole:  path|type|mode|uid|gid|size|nlink|mtime|major:minor|md5-or-link-target
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
