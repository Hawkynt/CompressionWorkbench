#!/bin/bash
# Boots the prepared Ubuntu s390x root under QEMU (TCG, 2 GiB) with init=/cwb-init.sh, runs generate-be-inner.sh
# against two blank 64 MiB pool disks, and copies the pools and the guest's manifests to <outdir>.
set -u
D="$(dirname "$(readlink -f "$0")")"
out="${1:?outdir}"; mkdir -p "$out"
Z=$HOME/oracles/zfsbe; W=$Z/run; mkdir -p $W; cd $W || exit 1
rm -f pool1.raw pool2.raw in.tar out.raw
truncate -s 64M pool1.raw; truncate -s 64M pool2.raw
mkdir -p in && cp "$D/generate-be-inner.sh" in/run.sh && tar -cf in.tar -C in run.sh && truncate -s 1M in.tar
truncate -s 16M out.raw
if ! ls $Z/config-* >/dev/null 2>&1; then
  LOOP=$(echo 1234 | sudo -S -p '' losetup -fP --show $Z/root.raw); mkdir -p $Z/mnt
  echo 1234 | sudo -S -p '' mount -o ro ${LOOP}p1 $Z/mnt && cp $Z/mnt/boot/config-* $Z/ ; echo 1234 | sudo -S -p '' umount $Z/mnt; echo 1234 | sudo -S -p '' losetup -d $LOOP
fi
# the kernel's own drivers suffice when virtio-blk, virtio-ccw and ext4 are built in; else boot through the initrd
CFG=$(ls $Z/config-* 2>/dev/null | head -1)
INITRD=""; APPEND="${ZFSBE_APPEND:-}"
if [ -n "$CFG" ]; then grep -E "^CONFIG_(VIRTIO_BLK|VIRTIO_CCW|EXT4_FS|S390_CCW_IOMMU|SCLP_CONSOLE)=" $CFG; fi
if [ -z "$CFG" ] || ! grep -q "^CONFIG_VIRTIO_BLK=y" $CFG || ! grep -q "^CONFIG_EXT4_FS=y" $CFG; then INITRD="-initrd $Z/initrd"; APPEND="$APPEND rootdelay=600"; fi
echo "initrd: ${INITRD:-none}"
timeout ${ZFSBE_TIMEOUT:-1050} qemu-system-s390x -M s390-ccw-virtio -cpu max -smp 4 -m 2048 -nographic -no-reboot \
  -kernel $Z/vmlinuz $INITRD -append "root=/dev/vda1 rootwait rw init=/cwb-init.sh console=ttysclp0 panic=-1 $APPEND" \
  -drive file=$Z/root.raw,format=raw,if=virtio,cache=unsafe \
  -drive file=pool1.raw,format=raw,if=virtio,cache=unsafe \
  -drive file=pool2.raw,format=raw,if=virtio,cache=unsafe \
  -drive file=in.tar,format=raw,if=virtio \
  -drive file=out.raw,format=raw,if=virtio > console.log 2>&1
echo "qemu rc=$?"
sed -n '/===GUEST-BEGIN===/,/===GUEST-END===/p' console.log | tr -d '\r' | head -80
grep -q "===GUEST-END" console.log || { grep -a -i -E "vda|virtio|panic|VFS|ccw|blk" console.log | head -30; tail -15 console.log; } | tr -d '\r'
mkdir -p o && tar -xf out.raw -C o 2>/dev/null; ls -la o
cp o/* "$out/" 2>/dev/null
gzip -9 -n -c pool1.raw > "$out/bebe.img.gz"; gzip -9 -n -c pool2.raw > "$out/beold.img.gz"
ls -la "$out"
