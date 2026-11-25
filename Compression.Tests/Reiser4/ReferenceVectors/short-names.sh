# Guest script for the short-key volumes: adds names on both sides of the lengths a short key holds
# whole (15 bytes) and a large key would (23 bytes), then runs populate.sh from the payload.
mount -t reiser4 /dev/vda /mnt || { echo MOUNTFAIL; exit 1; }
mkdir /mnt/lengths
for n in seven-7 eight-08 fifteen-bytes-x sixteen-bytes-xy twenty-three-bytes-xyz1 twenty-four-bytes-xyz123 \
         fifteen-bytes-y sixteen-bytes-yz; do
  echo "$n" > /mnt/lengths/$n
done
umount /mnt || echo UMOUNTFAIL
sh /payload/populate.sh
