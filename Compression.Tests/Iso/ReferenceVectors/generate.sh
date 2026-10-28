#!/bin/bash
# Regenerates the Rock Ridge reference images in this directory. Documentation, not a
# build input. Needs xorriso and guestfish (libguestfs-tools); no root.
#
# xorriso writes the images; the Linux kernel's isofs driver (inside the libguestfs
# appliance) reads them back, resolving Rock Ridge names, symbolic links, relocated deep
# directories and zisofs compression on its own. manifest-*.txt is what it saw.
set -euo pipefail
OUT=$(cd "$(dirname "$0")" && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
T="$WORK/tree"
mkdir -p "$T/sub" "$T/a/b/c/d/e/f/g/h/i/j"

python3 - "$T" <<'PY'
import os, random, sys
t = sys.argv[1]
r = random.Random(20261019)
words = [b"rock ", b"ridge ", b"zisofs ", b"block ", b"pointer\n"]
open(os.path.join(t, "big.txt"), "wb").write(b"".join(r.choice(words) for _ in range(60000)))
open(os.path.join(t, "zeros.bin"), "wb").write(bytes(200000))
open(os.path.join(t, "noise.bin"), "wb").write(bytes(r.getrandbits(8) for _ in range(40000)))
open(os.path.join(t, "sub", "small.txt"), "wb").write(b"small file\n")
open(os.path.join(t, "a/b/c/d/e/f/g/h/i/j", "deep.txt"), "wb").write(b"nine levels down\n")
long = "a-rather-long-rock-ridge-name-" * 7 + "end.txt"
open(os.path.join(t, long), "wb").write(b"long name\n")
PY
ln -s sub/small.txt "$T/rel-link"
ln -s /usr/share/doc "$T/abs-link"
ln -s ../big.txt "$T/sub/up-link"
ln -s does/not/exist "$T/dangling"

# zisofs on the compressible files only; noise.bin stays plain.
common=(-for_backup -rockridge on -compliance no_force_dots
        -map "$T" / -set_filter --zisofs /big.txt /zeros.bin --)
xorriso -outdev "$WORK/rr.iso" -joliet off "${common[@]}" -commit 2>/dev/null
xorriso -outdev "$WORK/rr-joliet.iso" -joliet on "${common[@]}" -commit 2>/dev/null
# mkisofs emulation relocates the 11-level directory into rr_moved with RE/CL entries.
xorriso -as mkisofs -R -o "$WORK/rr-moved.iso" "$T" 2>/dev/null

inner=$(cat <<'EOF' | base64 -w0
cd /sysroot
find . -mindepth 1 -printf '%y %P|%l\n' | LC_ALL=C sort
find . -type f -exec sha256sum {} + | sed 's|  \./| |' | LC_ALL=C sort -k2
EOF
)
for image in rr rr-joliet rr-moved; do
  guestfish --ro -a "$WORK/$image.iso" run : mount-ro /dev/sda / : debug sh "echo $inner | base64 -d | sh" \
    > "$OUT/manifest-$image.txt"
  gzip -9 -n -c "$WORK/$image.iso" > "$OUT/$image.iso.gz"
done
ls -la "$OUT"
