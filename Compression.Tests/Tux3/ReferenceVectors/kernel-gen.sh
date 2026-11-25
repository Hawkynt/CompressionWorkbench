#!/bin/bash
# Regenerates kernel.img.gz: `tux3 mkfs` (the user tools, TUX3_TOOLS = their directory) formats a
# 64 MiB volume, the linux-tux3 kernel fills it through its own write path (kernel-populate.sh, run
# by ../KernelOracle/kernel-run.sh), and the kernel's read-back is kept as kernel.manifest.
# Documentation, not a build input; run from this directory under Linux or WSL.
. "$(dirname "$(readlink -f "$0")")/../../Support/cwb-rmtree.sh"
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
RUN="$HERE/../KernelOracle/kernel-run.sh"
TOOLS=${TUX3_TOOLS:-$HOME/.cache/cwb/tux3-kernel/tools}
bash "$RUN" --prepare
W=$(mktemp -d)
trap 'cwb_rmtree "$W"' EXIT
mkdir -p "$W/payload"
for n in 0 1 100 4095 4096 4097 70000; do head -c $n /dev/urandom > "$W/payload/f_$n"; done
truncate -s 64M "$W/kernel.img"
"$TOOLS/tux3" mkfs "$W/kernel.img" > /dev/null
bash "$RUN" "$W/kernel.img" "$HERE/kernel-populate.sh" "$W/payload" > "$W/kernel.log"
sed -n '/===MANIFEST===/,/===END-MANIFEST===/p' "$W/kernel.log" | grep -v '===' > "$HERE/kernel.manifest"
gzip -9 -n -c "$W/kernel.img" > "$HERE/kernel.img.gz"
echo "kernel: $(wc -l < "$HERE/kernel.manifest") objects"
