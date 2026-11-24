#!/bin/bash
# Prepares an Ubuntu 24.04 s390x root file system for a direct-kernel QEMU boot:
# installs zfsutils-linux inside it (through qemu-user), extracts its kernel and initrd.
set -u
Z=$HOME/oracles/zfsbe
cd $Z || exit 1
SUDO() { echo 1234 | sudo -S -p '' "$@"; }
ls /proc/sys/fs/binfmt_misc/ | grep -i s390 || SUDO update-binfmts --enable qemu-s390x 2>&1 | tail -2
ls /proc/sys/fs/binfmt_misc/ | grep -i s390
if [ ! -f root.raw ]; then
  qemu-img convert -O raw noble-s390x.img root.raw && qemu-img resize -f raw root.raw 6G >/dev/null
fi
LOOP=$(SUDO losetup -fP --show root.raw); echo "loop $LOOP"; lsblk $LOOP
SUDO growpart $LOOP 1 2>&1 | tail -1; SUDO e2fsck -fy ${LOOP}p1 >/dev/null 2>&1; SUDO resize2fs ${LOOP}p1 2>&1 | tail -1
M=$Z/mnt; mkdir -p $M
SUDO mount ${LOOP}p1 $M || { SUDO losetup -d $LOOP; exit 1; }
ls $M/boot; ls $M/lib/modules/
find $M/lib/modules -name "zfs.ko*" -o -name "spl.ko*" | head
SUDO mount --bind /proc $M/proc; SUDO mount --bind /dev $M/dev; SUDO mount --bind /sys $M/sys
SUDO cp /etc/resolv.conf $M/etc/resolv.conf.cwb
SUDO chroot $M /bin/sh -c 'mv /etc/resolv.conf /etc/resolv.conf.orig 2>/dev/null; cp /etc/resolv.conf.cwb /etc/resolv.conf; uname -m; export DEBIAN_FRONTEND=noninteractive; apt-get update -q >/dev/null 2>&1; apt-get install -y -q zfsutils-linux >/tmp/apt.log 2>&1; echo apt=$?; tail -3 /tmp/apt.log; command -v zpool zfs zdb; zfs version 2>&1 | head -2'
cat > $Z/cwb-init.sh <<'INIT'
#!/bin/sh
# QEMU guest init: runs run.sh from the tar on /dev/vdd, writes everything under /tmp/out as a tar to /dev/vde
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
mount -t proc proc /proc; mount -t sysfs sys /sys; mount -t devtmpfs dev /dev; mount -t tmpfs tmp /tmp
mkdir -p /tmp/in /tmp/out; tar -xf /dev/vdd -C /tmp/in
echo ===GUEST-BEGIN===; sh /tmp/in/run.sh > /tmp/out/log 2>&1; echo rc=$? >> /tmp/out/log; cat /tmp/out/log; echo ===GUEST-END===
tar -cf /dev/vde -C /tmp/out .; sync
echo o > /proc/sysrq-trigger; poweroff -f
INIT
SUDO cp $Z/cwb-init.sh $M/cwb-init.sh; SUDO chmod 755 $M/cwb-init.sh
K=$(ls $M/boot/vmlinuz-* | sort | tail -1); I=$(ls $M/boot/initrd.img-* | sort | tail -1)
echo "kernel $K initrd $I"
SUDO cp $K $Z/vmlinuz; SUDO cp $I $Z/initrd; SUDO chown $(id -u) $Z/vmlinuz $Z/initrd
SUDO umount $M/proc $M/dev $M/sys; SUDO umount $M; SUDO losetup -d $LOOP
ls -la $Z
