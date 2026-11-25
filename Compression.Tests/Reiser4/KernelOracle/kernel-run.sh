#!/bin/bash
# Boots a Reiser4-enabled Linux kernel under QEMU with an image as /dev/vda and runs a shell
# script inside it; the script's output is printed between ===GUEST-BEGIN=== and ===GUEST-END===.
#
#   kernel-run.sh <image> <guest-script> [payload-dir]
#
# The kernel is Metztli's Debian build of Linux 5.4.18 with the Reiser4 (SFRN 4.0.2) patch
# (linux-image-5.4.0-3+reizer4.0.2-cloud-amd64). It is fetched once into $CWB_REISER4_KERNEL
# (default ~/.cache/cwb/reiser4-kernel) together with Ubuntu's busybox-static for the initramfs.
# Nothing here is a build input: the tests that use it skip when it cannot run.
#
# CWB_REISER4_BZIMAGE names a kernel image with Reiser4 and virtio built in instead (no modules are
# loaded then); the short-key tests use one, because only a kernel compiled with
# REISER4_LARGE_KEY=0 mounts short-key volumes (docs/REISER4-NATIVE-PROFILE.md, "Short keys").
#
# Exit status of a run, so the caller can tell a broken harness or host from a broken image:
#   0  the guest booted, ran the script and printed ===GUEST-END===
#   2  the guest never reached ===GUEST-BEGIN=== (qemu or the kernel did not come up: the host)
#   3  the harness is incomplete (a helper this script needs is missing: the repository)
#   4  the guest came up and mounted the image but did not finish: it hung, crashed or ran out of
#      time inside the guest (the image, or a time limit too tight: ===QEMU rc=124=== says which)
# ===QEMU rc=N timeout=T=== reports qemu's own status and the time limit (CWB_REISER4_KERNEL_TIMEOUT,
# default 1200 s) whatever happened.
set -e
K=${CWB_REISER4_KERNEL:-$HOME/.cache/cwb/reiser4-kernel}
KV=5.4.0-3+reizer4.0.2-cloud-amd64
DEB_URL='https://sourceforge.net/projects/metztli-reiser4/files/RyzenEpyc_Reiser4-SFRN-4.0.2_Linux-5.4.18-1_for-Debian_Buster/linux-image-5.4.0-3%2Breizer4.0.2-cloud-amd64_5.4.18-1%2Breizer4.0.2_amd64.deb/download'

BZ=${CWB_REISER4_BZIMAGE:-}
if [ "$1" = "--check" ]; then
  if [ -n "$BZ" ]; then [ -f "$BZ" ] && [ -x "$K/bb/usr/bin/busybox" ] && command -v qemu-system-x86_64 >/dev/null; exit $?; fi
  [ -f "$K/x/boot/vmlinuz-$KV" ] && [ -x "$K/bb/usr/bin/busybox" ] && command -v qemu-system-x86_64 >/dev/null
  exit $?
fi
if [ "$1" = "--prepare" ]; then
  mkdir -p "$K"
  if [ ! -f "$K/x/boot/vmlinuz-$KV" ]; then
    curl -sfL -A "Wget/1.21" -o "$K/kernel.deb" "$DEB_URL"
    mkdir -p "$K/x" && dpkg-deb -x "$K/kernel.deb" "$K/x"
  fi
  if [ ! -x "$K/bb/usr/bin/busybox" ]; then
    (cd "$K" && apt-get download busybox-static >/dev/null 2>&1 && mkdir -p bb && dpkg-deb -x busybox-static*.deb bb)
  fi
  [ -f "$K/x/boot/vmlinuz-$KV" ] && [ -x "$K/bb/usr/bin/busybox" ] && command -v qemu-system-x86_64 >/dev/null
  exit $?
fi

HELPER="$(dirname "$(readlink -f "$0")")/../../Support/cwb-rmtree.sh"
if [ ! -f "$HELPER" ]; then echo "HARNESS-INCOMPLETE: $HELPER is missing"; exit 3; fi
. "$HELPER"

M=$K/x/lib/modules/$KV/kernel
R=$(mktemp -d)
# Cleanup must never decide the outcome: a failing removal is reported, not returned.
trap 'cwb_rmtree "$R" "$R.cpio.gz" "$R.log" || echo "cleanup of $R failed" >&2' EXIT
mkdir -p "$R/bin" "$R/mods" "$R/dev" "$R/proc" "$R/sys" "$R/mnt" "$R/payload"
cp "$K/bb/usr/bin/busybox" "$R/bin/"
for a in $("$K/bb/usr/bin/busybox" --list); do [ "$a" = busybox ] || ln -s busybox "$R/bin/$a"; done
[ -z "$BZ" ] && for m in drivers/virtio/virtio.ko drivers/virtio/virtio_ring.ko drivers/virtio/virtio_pci.ko drivers/block/virtio_blk.ko \
         lib/xxhash.ko lib/zstd/zstd_compress.ko lib/zstd/zstd_decompress.ko crypto/crc32c_generic.ko fs/reiser4/reiser4.ko; do
  cp "$M/$m" "$R/mods/"
done
cp "$2" "$R/run.sh"
[ -n "$3" ] && cp -a "$3"/. "$R/payload/"
cat > "$R/init" <<'EOF'
#!/bin/busybox sh
export PATH=/bin
mount -t proc proc /proc; mount -t sysfs sys /sys; mount -t devtmpfs dev /dev 2>/dev/null
echo "===GUEST-BEGIN==="
[ -f /mods/reiser4.ko ] && for m in virtio virtio_ring virtio_pci virtio_blk xxhash zstd_compress zstd_decompress crc32c_generic reiser4; do
  insmod /mods/$m.ko || echo "insmod $m failed"
done
sleep 0.3
sh /run.sh 2>&1
echo "===GUEST-END rc=$?==="
dmesg | grep -i -E "reiser4|bug|oops" | grep -v "loading out-of-tree" | sed 's/^/dmesg: /'
sync
poweroff -f
EOF
chmod +x "$R/init"
(cd "$R" && find . | cpio -o -H newc 2>/dev/null | gzip -1) > "$R.cpio.gz"
ACC=-enable-kvm; [ -w /dev/kvm ] || ACC="-accel tcg"
T=${CWB_REISER4_KERNEL_TIMEOUT:-1200}
set +e
timeout "$T" qemu-system-x86_64 $ACC -m 1024 -nographic -no-reboot \
  -kernel "${BZ:-$K/x/boot/vmlinuz-$KV}" -initrd "$R.cpio.gz" \
  -append "console=ttyS0 quiet panic=-1" \
  -drive file="$1",format=raw,if=virtio,cache=unsafe 2>&1 | tr -d '\r' > "$R.log"
QRC=${PIPESTATUS[0]}
set -e
sed -n '/===GUEST-BEGIN===/,$p' "$R.log" | grep -v '^\s*$' || true
echo "===QEMU rc=$QRC timeout=$T accel=$ACC==="
if ! grep -q '===GUEST-BEGIN===' "$R.log"; then
  echo "GUEST-NOT-BOOTED"
  tail -n 40 "$R.log" | sed 's/^/console: /'
  exit 2
fi
if ! grep -q '===GUEST-END' "$R.log"; then
  echo "GUEST-DID-NOT-FINISH"
  exit 4
fi
exit 0
