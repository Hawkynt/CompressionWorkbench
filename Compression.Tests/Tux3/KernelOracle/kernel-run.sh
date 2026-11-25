#!/bin/bash
# Boots the linux-tux3 kernel under QEMU with an image as /dev/vda and runs a shell script inside
# it; the script's output is printed between ===GUEST-BEGIN=== and ===GUEST-END===.
#
#   kernel-run.sh <image> <guest-script> [payload-dir]
#   kernel-run.sh --check | --prepare
#
# The kernel is built from the linux-tux3 tree (docs/TUX3-ON-DISK.md, "Kernel oracle") with Tux3
# and virtio built in; CWB_TUX3_BZIMAGE names it (default ~/.cache/cwb/tux3-kernel/bzImage). The
# initramfs is Ubuntu's busybox-static, fetched once by --prepare. Nothing here is a build input:
# the tests that use it skip when it cannot run.
#
# Exit status: 0 the guest ran the script and printed ===GUEST-END===; 2 the guest never reached
# ===GUEST-BEGIN=== (the host); 4 the guest came up but did not finish (the image, or a time limit
# too tight; CWB_TUX3_KERNEL_TIMEOUT, default 600 s); 3 the harness is incomplete.
set -e
K=${CWB_TUX3_KERNEL:-$HOME/.cache/cwb/tux3-kernel}
BZ=${CWB_TUX3_BZIMAGE:-$K/bzImage}

if [ "$1" = "--check" ] || [ "$1" = "--prepare" ]; then
  if [ "$1" = "--prepare" ] && [ ! -x "$K/bb/usr/bin/busybox" ]; then
    mkdir -p "$K"
    (cd "$K" && apt-get download busybox-static >/dev/null 2>&1 && mkdir -p bb && dpkg-deb -x busybox-static*.deb bb)
  fi
  [ -f "$BZ" ] && [ -x "$K/bb/usr/bin/busybox" ] && command -v qemu-system-x86_64 >/dev/null
  exit $?
fi

HELPER="$(dirname "$(readlink -f "$0")")/../../Support/cwb-rmtree.sh"
if [ ! -f "$HELPER" ]; then echo "HARNESS-INCOMPLETE: $HELPER is missing"; exit 3; fi
. "$HELPER"
R=$(mktemp -d)
trap 'cwb_rmtree "$R" "$R.cpio.gz" "$R.log" || echo "cleanup of $R failed" >&2' EXIT
mkdir -p "$R/bin" "$R/dev" "$R/proc" "$R/sys" "$R/mnt" "$R/payload"
cp "$K/bb/usr/bin/busybox" "$R/bin/"
for a in $("$K/bb/usr/bin/busybox" --list); do [ "$a" = busybox ] || ln -s busybox "$R/bin/$a"; done
cp "$2" "$R/run.sh"
[ -n "${3:-}" ] && cp -a "$3"/. "$R/payload/"
cat > "$R/init" <<'EOF'
#!/bin/busybox sh
export PATH=/bin
mount -t proc proc /proc; mount -t sysfs sys /sys; mount -t devtmpfs dev /dev 2>/dev/null
echo "===GUEST-BEGIN==="
sleep 0.3
sh /run.sh 2>&1
echo "===GUEST-END rc=$?==="
dmesg | grep -i -E "tux3|bug|oops|warning" | sed 's/^/dmesg: /'
sync
poweroff -f
EOF
chmod +x "$R/init"
(cd "$R" && find . | cpio -o -H newc 2>/dev/null | gzip -1) > "$R.cpio.gz"
ACC=-enable-kvm; [ -w /dev/kvm ] || ACC="-accel tcg"
T=${CWB_TUX3_KERNEL_TIMEOUT:-600}
set +e
timeout "$T" qemu-system-x86_64 $ACC -m 1024 -nographic -no-reboot \
  -kernel "$BZ" -initrd "$R.cpio.gz" -append "console=ttyS0 quiet panic=-1" \
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
grep -q '===GUEST-END' "$R.log" || { echo "GUEST-DID-NOT-FINISH"; exit 4; }
exit 0
