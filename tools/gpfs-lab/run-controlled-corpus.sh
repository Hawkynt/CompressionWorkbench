#!/usr/bin/env bash
set -euo pipefail

FS=${GPFS_FS:-fs1}
MOUNT=${GPFS_MOUNT:-/ibm/fs1}
ROOT=${GPFS_CORPUS_ROOT:-/var/tmp/cw-gpfs-corpus}
WORK="$MOUNT/.cw-gpfs-corpus"
CAPTURE=$(readlink -f "$(dirname "$0")/capture.sh")

[[ ${EUID} -eq 0 ]] || { echo "run-controlled-corpus.sh must run as root" >&2; exit 2; }
for cmd in python3 fallocate setfattr setfacl mmlsfs mmrestripefile mmchattr mmlsattr mmgetlocation; do
  command -v "$cmd" >/dev/null || { echo "missing: $cmd" >&2; exit 2; }; done
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

storage_pool() {
  mmlsattr -L "$1" | awk -F ':' '
    tolower($1) ~ /storage pool name/ {
      value=$2
      gsub(/^[ \t]+|[ \t]+$/, "", value)
      print value
      exit
    }'
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
[[ $(storage_pool "$target") == capacity ]] || {
  echo "IBM demo placement policy did not place $target in capacity pool" >&2
  exit 4
}
capture 050-block-plus-one "cross full-block boundary by one byte in capacity pool" "$anchor" "$target"

# Force a different physical disk set instead of hoping an ordinary balance run
# moves this tiny file. IBM's demo policy places ordinary files in capacity;
# assigning system with deferred migration and then -p must move its blocks from
# nsd6/nsd7 to the system-pool NSDs if the operation succeeds.
mmgetlocation -f "$target" -Y -L >"$ROOT/location-before-rebalance.txt"
mmchattr -P system -I defer "$target"
mmrestripefile -p "$target"
[[ $(storage_pool "$target") == system ]] || {
  echo "GPFS did not migrate $target to the system pool" >&2
  exit 4
}
mmgetlocation -f "$target" -Y -L >"$ROOT/location-after-rebalance.txt"
capture 055-rebalanced "force capacity-to-system relocation for disk-address derivation" "$anchor" "$target"

target="$WORK/indirect.bin"
fallocate -l "$((block * 331 + 1))" "$target"
# Defeat all-zero/sparse ambiguity while retaining more allocated positions than
# the direct address-slot count exposed by the IBM inode oracle.
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

replicated="$WORK/replicated.bin"
write_pattern "$replicated" "$((block * 2 + 97))"
# IBM documents -R/-r as maximum/current data replicas and -I yes as immediate
# replication. The capture records tsdbfs/mmgetlocation after the operation; the
# raw verifier still has to prove that multiple replicas were actually created.
mmchattr -R 2 -r 2 -I yes "$replicated"
mmrestripefile -r "$replicated"
mmlsattr -L "$replicated" >"$ROOT/mmlsattr-replicated.txt"
capture 105-replicated "create a two-replica data file" "$anchor" "$replicated"

rm "$WORK/one-subblock.bin"
capture 110-delete "delete the earlier one-subblock file" "$anchor"

# Inode allocation map: 110 is the baseline. Creating an empty file allocates an
# inode but no user-data subblock. Diff only the IBM-located reserved inode-2
# contents; directory metadata changes are deliberately outside that byte range.
inode_probe="$WORK/inode-map-probe"
: >"$inode_probe"
capture 120-inode-allocated "allocate exactly one fresh empty-file inode" "$anchor" "$inode_probe"
rm "$inode_probe"
capture 121-inode-freed "free exactly the preceding empty-file inode" "$anchor"

# Block allocation map: establish a baseline with an already allocated inode,
# then change only that file's data allocation. This decouples the block-map bit
# from inode allocation and lets 130->131 and 131->132 act as inverse transitions.
block_probe="$WORK/block-map-probe.bin"
: >"$block_probe"
capture 130-block-baseline "keep block-map probe inode allocated with no file data" "$anchor" "$block_probe"
write_pattern "$block_probe" "$subblock"
capture 131-block-allocated "allocate exactly one data subblock to existing inode" "$anchor" "$block_probe"
truncate -s 0 "$block_probe"
capture 132-block-freed "free exactly the preceding data subblock without freeing inode" "$anchor" "$block_probe"

printf 'corpus %s complete under %s\n' "$CORPUS_ID" "$ROOT"
printf 'Do not promote GPFS from these captures alone: reformat/reprovision for a distinct filesystem UID, run a second corpus, then verify raw-parser agreement.\n'
