#!/bin/bash
# Regenerates ntfs-wof.img.gz: an NTFS volume holding Windows Overlay Filter
# ("system compression") files. Documentation, not a build input.
#
#   generate.sh <payload-dir>
#
# <payload-dir> holds what WofPayloadGenerator.cs wrote on Windows: source.bin and the
# WofCompressedData streams xpress4k.wof, xpress8k.wof, xpress16k.wof and lzx.wof.
# Needs mkntfs (ntfs-3g) and guestfish (libguestfs-tools); no root.
#
# Inside the libguestfs appliance ntfs-3g plants each file the way WOF lays it out — an
# unnamed $DATA of the original length (written as zeros: ntfs3 reads nothing past its
# valid size), the payload in the named stream WofCompressedData, and an
# IO_REPARSE_TAG_WOF reparse point naming the algorithm — and
# then the kernel's ntfs3 driver, which decodes system compression on its own, reads every
# file back. manifest.txt is what ntfs3 returned.
set -euo pipefail
OUT=$(cd "$(dirname "$0")" && pwd)
SRC=$(cd "$1" && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
size=$(stat -c %s "$SRC/source.bin")

truncate -s 16M "$WORK/ntfs.img"
mkntfs -q -F -f -L cwb-wof "$WORK/ntfs.img"

# REPARSE_DATA_BUFFER: tag 0x80000017, data length 16, reserved; WOF_EXTERNAL_INFO
# {Version 1, Provider 2 = FILE}; FILE_PROVIDER_EXTERNAL_INFO_V1 {Version 1, Algorithm}.

inner=$(cat <<EOF | base64 -w0
set -e
mkdir -p /w
mount -t ntfs-3g -o streams_interface=windows /dev/sda /w
cp /src/source.bin /w/plain.bin
for spec in xpress4k:0 lzx:1 xpress8k:2 xpress16k:3; do
  name=\${spec%%:*}; alg=\${spec##*:}
  head -c $size /dev/zero > /w/\$name.bin
  cat /src/\$name.wof > "/w/\$name.bin:WofCompressedData"
  setfattr -h -n system.ntfs_reparse_data -v \$(printf '0x17000080100000000100000002000000010000000%s000000' \$alg) /w/\$name.bin
done
umount /w
mount -t ntfs3 -o ro /dev/sda /w
cd /w
for f in plain.bin xpress4k.bin xpress8k.bin xpress16k.bin lzx.bin; do
  echo "\$(sha256sum \$f | cut -d' ' -f1) \$(stat -c %s \$f) \$f"
done
cd /
umount /w
EOF
)

# The appliance root is not persistent; payloads travel in through a second disk.
truncate -s 8M "$WORK/payload.img"
mkfs.ext2 -q -F -d "$SRC" "$WORK/payload.img"
guestfish --rw -a "$WORK/ntfs.img" -a "$WORK/payload.img" run : \
  debug sh "mkdir -p /src && mount -o ro /dev/sdb /src && echo $inner | base64 -d | sh" > "$WORK/manifest.txt"
cat "$WORK/manifest.txt"
grep -q "$(sha256sum "$SRC/source.bin" | cut -d' ' -f1) $size lzx.bin" "$WORK/manifest.txt"
cp "$WORK/manifest.txt" "$OUT/manifest.txt"
gzip -9 -n -c "$WORK/ntfs.img" > "$OUT/ntfs-wof.img.gz"
