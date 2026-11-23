#!/bin/bash
# Mounts an AXFS image with the AXFS kernel driver (jaredeh/axfs, built as a module for Debian
# buster's Linux 4.19) under QEMU and prints one line per object the kernel resolves, reading every
# regular file whole:
#
#   F|path|mode|uid|gid|size|md5     D|path|mode|uid|gid     L|path|mode|uid|gid|target
#
# between ===MANIFEST=== and ===END-MANIFEST===.
#
#   axfs-run.sh --prepare     fetch the kernel, headers and driver once, build the module
#   axfs-run.sh --check       exit 0 when everything is in place
#   axfs-run.sh <image>       mount <image> as /dev/vda and print the manifest
#
# Everything lives in $CWB_AXFS_KERNEL (default ~/.cache/cwb/axfs-kernel). The busybox for the
# initramfs is Ubuntu's busybox-static. Nothing here is a build input: the tests skip when it
# cannot run.
. "$(dirname "$(readlink -f "$0")")/../../Support/cwb-rmtree.sh"
set -e
K=${CWB_AXFS_KERNEL:-$HOME/.cache/cwb/axfs-kernel}
KV=4.19.0-21-amd64
DEB=http://archive.debian.org/debian/pool/main/l/linux
AXFS_REPO=https://github.com/jaredeh/axfs.git
AXFS_REV=2148079b749e8a56217f30a8bd446bea543838d0
PACKAGES="c241d3109862fbf4b526afdc72faee4e06cab0f90743acf4be724fafab10235d linux-image-4.19.0-21-amd64-unsigned_4.19.249-2_amd64.deb
7e8c7a74acbe6939d32b8aacd58f552730e5c160a553e6757569619c6f603613 linux-headers-4.19.0-21-amd64_4.19.249-2_amd64.deb
81671a059fe02b5f74cc585f08df053d60a890041b60b7548cdfaf1a2f4dfde0 linux-headers-4.19.0-21-common_4.19.249-2_all.deb
ed9203f2a7d1eaa7ff92c0f18a4afb066633f71eb37ac59a818a4d6c802cb33d linux-kbuild-4.19_4.19.249-2_amd64.deb"

ready() {
  [ -f "$K/x/boot/vmlinuz-$KV" ] && [ -f "$K/mod/axfs.ko" ] && [ -x "$K/bb/usr/bin/busybox" ] && command -v qemu-system-x86_64 >/dev/null
}

if [ "$1" = "--check" ]; then
  ready
  exit $?
fi
if [ "$1" = "--prepare" ]; then
  mkdir -p "$K" && cd "$K"
  echo "$PACKAGES" | while read -r sum f; do
    [ -f "$f" ] || curl -sfL -o "$f" "$DEB/$f"
    echo "$sum  $f" | sha256sum -c --quiet
    dpkg-deb -x "$f" x
  done
  # Debian's headers refer to /usr/src and /usr/lib by absolute path; point them into $K/x.
  H=$K/x/usr/src/linux-headers-$KV
  for l in "$H/scripts" "$H/tools" $(find "$K/x/usr/src/linux-headers-4.19.0-21-common" -maxdepth 1 -type l); do
    t=$(readlink "$l"); case "$t" in /usr/*) ln -sfn "$K/x$t" "$l";; esac
  done
  grep -q "$K/x/usr/src" "$H/Makefile" || sed -i "s|/usr/src/|$K/x/usr/src/|g" "$H/Makefile"
  [ -d src ] || git clone -q "$AXFS_REPO" src
  git -C src checkout -q "$AXFS_REV"
  rm -rf mod && cp -r src/kernel/fs/axfs mod
  # The driver's mmap fault path calls lock_page_or_retry, whose helper 4.19 does not export to
  # modules; take the page lock outright instead (reads through read() never get there).
  sed -i 's/!lock_page_or_retry(page, vma->vm_mm, vmf->flags)/(lock_page(page), 0)/' mod/axfs_inode.c
  # Block-device reads take the remainder of do_div as the block number, where do_div leaves the
  # quotient in its argument; without this every read of a mounted block device lands on the
  # wrong block. The quotient is the block.
  sed -i 's/block = do_div(block, blksize);/do_div(block, blksize);/' mod/axfs_bdev.c
  grep -q '^\s*do_div(block, blksize);' mod/axfs_bdev.c
  make -s -C "$H" M="$K/mod" CC=gcc CONFIG_AXFS=m modules >/dev/null
  if [ ! -x "$K/bb/usr/bin/busybox" ]; then
    (cd "$K" && apt-get download busybox-static >/dev/null 2>&1 && mkdir -p bb && dpkg-deb -x busybox-static*.deb bb)
  fi
  ready
  exit $?
fi

M=$K/x/lib/modules/$KV/kernel
R=$(mktemp -d)
trap 'cwb_rmtree "$R" "$R.cpio.gz" "$R.log" "$R.img"' EXIT
mkdir -p "$R/bin" "$R/mods" "$R/dev" "$R/proc" "$R/sys" "$R/mnt"
cp "$K/bb/usr/bin/busybox" "$R/bin/"
for a in $("$K/bb/usr/bin/busybox" --list); do [ "$a" = busybox ] || ln -s busybox "$R/bin/$a"; done
for m in drivers/virtio/virtio.ko drivers/virtio/virtio_ring.ko drivers/virtio/virtio_pci.ko drivers/block/virtio_blk.ko; do
  cp "$M/$m" "$R/mods/"
done
cp "$K/mod/axfs.ko" "$R/mods/"
cat > "$R/init" <<'EOF'
#!/bin/busybox sh
export PATH=/bin
mount -t proc proc /proc; mount -t sysfs sys /sys; mount -t devtmpfs dev /dev
echo "===GUEST-BEGIN==="
for m in virtio virtio_ring virtio_pci virtio_blk axfs; do
  insmod /mods/$m.ko || echo "insmod $m failed"
done
sleep 0.3
if mount -t axfs -o ro /dev/vda /mnt; then
  cd /mnt
  echo "===MANIFEST==="
  find . | sort | while read p; do
    s=$(stat -c '%a|%u|%g' "$p")
    if [ -L "$p" ]; then echo "L|$p|$s|$(readlink "$p")"
    elif [ -d "$p" ]; then echo "D|$p|$s"
    elif [ -f "$p" ]; then echo "F|$p|$s|$(stat -c %s "$p")|$(md5sum < "$p" | cut -d' ' -f1)"
    else echo "S|$p|$s|$(stat -c '%F|%t:%T' "$p")"; fi
  done
  echo "===END-MANIFEST==="
  cd /
  umount /mnt || echo UMOUNTFAIL
else
  echo MOUNTFAIL
fi
echo "===GUEST-END==="
dmesg | grep -E "axfs|BUG|Oops|Call Trace" | grep -v "loading out-of-tree\|taints kernel" | sed 's/^/dmesg: /'
poweroff -f
EOF
chmod +x "$R/init"
(cd "$R" && find . | cpio -o -H newc 2>/dev/null | gzip -1) > "$R.cpio.gz"
# a block device holds whole sectors: pad a copy of the image to 4 KiB
cp "$1" "$R.img"
truncate -s %4096 "$R.img"
ACC=-enable-kvm; [ -w /dev/kvm ] || ACC="-accel tcg"
timeout ${CWB_AXFS_KERNEL_TIMEOUT:-600} qemu-system-x86_64 $ACC -m 512 -nographic -no-reboot \
  -kernel "$K/x/boot/vmlinuz-$KV" -initrd "$R.cpio.gz" \
  -append "console=ttyS0 quiet panic=-1" \
  -drive file="$R.img",format=raw,if=virtio,readonly=on 2>&1 | tr -d '\r' > "$R.log" || true
sed -n '/===GUEST-BEGIN===/,$p' "$R.log" | grep -v '^\s*$'
grep -q '===GUEST-END' "$R.log"
