#!/usr/bin/env bash
set -euo pipefail

FS=${GPFS_FS:-fs1}
MOUNT=${GPFS_MOUNT:-/ibm/fs1}
ROOT=${GPFS_CORPUS_ROOT:-/var/tmp/cw-gpfs-corpus}
WORK="$MOUNT/.cw-gpfs-corpus"
CAPTURE=$(readlink -f "$(dirname "$0")/capture.sh")

[[ ${EUID} -eq 0 ]] || { echo "run-controlled-corpus.sh must run as root" >&2; exit 2; }
for cmd in python3 fallocate setfattr setfacl mmlsfs; do command -v "$cmd" >/dev/null || { echo "missing: $cmd" >&2; exit 2; }; done
[[ -x $CAPTURE ]] || { echo "capture harness not found: $CAPTURE" >&2; exit 2; }

rm -rf "$WORK"
mkdir -p "$WORK" "$ROOT"
CORPUS_ID=${GPFS_CORPUS_ID:-$(hostname)-$(date -u +%Y%m%dT%H%M%SZ)}
export GPFS_CORPUS_ID=$CORPUS_ID GPFS_FS=$FS GPFS_MOUNT=$MOUNT

subblock=$(mmlsfs "$FS" -f | awk '$1 == "-f" { print $2; exit }')
block=$(mmlsfs "$FS" -B | awk '$1 == "-B" { print $2; exit }')
[[ $subblock =~ ^[0-9]+$ && $block =~ ^[0-9]+$ ]] || { echo "could not read GPFS block geometry" >&2; exit 3; }

write_pattern() {
  local path=$1 size=$2
  python3 - "$path" "$size" <<'PY'
import sys
path, size = sys.argv[1], int(sys.argv[2])
chunk = bytes((i * 131 + 17) & 0xff for i in range(1 << 20))
with open(path, 'wb') as f:
    while size:
        take = min(size, len(chunk))
        f.write(chunk[:take])
        size -= take
PY
}

capture() {
  local id=$1 operation=$2
  shift 2
  GPFS_CAPTURE_ID="$id" GPFS_OPERATION="$operation" "$CAPTURE" "$ROOT/$id" "$@"
}

anchor="$WORK/anchor.bin"
write_pattern "$anchor" 257
capture 000-anchor "create deterministic anchor" "$anchor"

target="$WORK/empty"
: >"$target"
capture 010-empty "create empty regular file" "$anchor" "$target"

target="$WORK/tiny.bin"
write_pattern "$target" 97
capture 020-tiny "create 97-byte deterministic file" "$anchor" "$target"

target="$WORK/one-subblock.bin"
write_pattern "$target" "$subblock"
capture 030-one-subblock "allocate exactly one subblock" "$anchor" "$target"

target="$WORK/one-block.bin"
write_pattern "$target" "$block"
capture 040-one-block "allocate exactly one full block" "$anchor" "$target"

target="$WORK/block-plus-one.bin"
write_pattern "$target" "$((block + 1))"
capture 050-block-plus-one "cross full-block boundary by one byte" "$anchor" "$target"

target="$WORK/indirect.bin"
fallocate -l "$((block * 331 + 1))" "$target"
# Defeat all-zero/sparse ambiguity while retaining one allocation per block.
for ((i=0; i<=331; ++i)); do printf '\xA5' | dd of="$target" bs=1 seek="$((i * block))" conv=notrunc status=none; done
capture 060-indirect "force more than 330 allocated block positions" "$anchor" "$target"

target="$WORK/sparse.bin"
truncate -s "$((block * 5 + 123))" "$target"
printf '\x31' | dd of="$target" bs=1 seek=0 conv=notrunc status=none
printf '\x73' | dd of="$target" bs=1 seek="$((block * 2 + 17))" conv=notrunc status=none
printf '\xC7' | dd of="$target" bs=1 seek="$((block * 5 + 122))" conv=notrunc status=none
capture 070-sparse "create sparse file with three known data islands" "$anchor" "$target"

dir="$WORK/growing-dir"
mkdir "$dir"
for i in $(seq -w 0 511); do printf '%s' "$i" >"$dir/e$i"; done
capture 080-directory "grow directory to 512 deterministic entries" "$anchor" "$dir/e000"

link_target="$WORK/link-target"
write_pattern "$link_target" 4097
ln "$link_target" "$WORK/hard-link"
ln -s link-target "$WORK/sym-link"
capture 090-links "add hard link and symbolic link" "$anchor" "$link_target" "$WORK/hard-link"

setfattr -n user.cw_gpfs_probe -v 'xattr-0123456789abcdef' "$link_target"
setfacl -m u:nobody:r-- "$link_target"
capture 100-xattr-acl "add deterministic xattr and ACL" "$anchor" "$link_target"

rm "$WORK/one-subblock.bin"
capture 110-delete "delete the isolated one-subblock file" "$anchor"

printf 'corpus %s complete under %s\n' "$CORPUS_ID" "$ROOT"
printf 'Do not promote GPFS from these captures alone: run a second independent corpus and verify raw-parser agreement.\n'
