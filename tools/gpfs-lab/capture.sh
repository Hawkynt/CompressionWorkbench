#!/usr/bin/env bash
set -euo pipefail

FS=${GPFS_FS:-fs1}
MOUNT=${GPFS_MOUNT:-/ibm/fs1}
OUT=${1:?usage: capture.sh OUT_DIR [PATH ...]}
shift
PROBES=("$@")
OPERATION=${GPFS_OPERATION:-manual}
CORPUS_ID=${GPFS_CORPUS_ID:-storage-scale-vagrant}
CAPTURE_ID=${GPFS_CAPTURE_ID:-$(date -u +%Y%m%dT%H%M%SZ)}

[[ ${EUID} -eq 0 ]] || { echo "capture.sh must run as root" >&2; exit 2; }
for cmd in mmlsfs mmlsdisk mmlsnsd mmfsckx tsdbfs mmgetlocation mmfileid mmumount mmmount sha256sum blockdev dd stat findmnt awk; do
  command -v "$cmd" >/dev/null || { echo "missing required command: $cmd" >&2; exit 2; }
done

mkdir -p "$OUT/oracle" "$OUT/raw"
OUT=$(readlink -f "$OUT")
manifest="$OUT/manifest.tsv"
: >"$manifest"

findmnt -rn -T "$MOUNT" -t gpfs >/dev/null 2>&1 || {
  echo "$FS must be mounted at $MOUNT before oracle collection" >&2
  exit 3
}

remount=0
cleanup() {
  if [[ $remount -eq 1 ]]; then
    mmmount "$FS" -a >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

mmlsfs "$FS" -Y >"$OUT/oracle/mmlsfs.txt"
mmlsfs "$FS" --uid >"$OUT/oracle/mmlsfs-uid.txt"
mmlsfs "$FS" -V >"$OUT/oracle/mmlsfs-version.txt"
mmlsdisk "$FS" -L -Y >"$OUT/oracle/mmlsdisk.txt"
mmlsdisk "$FS" -L >"$OUT/oracle/mmlsdisk-human.txt"
mmlsnsd -f "$FS" -m -Y >"$OUT/oracle/mmlsnsd.txt"
mmlsnsd -f "$FS" -m >"$OUT/oracle/mmlsnsd-human.txt"

# mmfsckx is an online checker. Run report-only while mounted, then take the raw
# images only after all oracle reads are complete and the filesystem is unmounted.
mmfsckx "$FS" --check-reserved-files-only >"$OUT/oracle/mmfsckx.txt" 2>&1

for inode in 0 1 2 4 5 38; do
  tsdbfs "$FS" inode "$inode" >"$OUT/oracle/tsdbfs-reserved-${inode}.txt"
done

probe_count=0
for path in "${PROBES[@]}"; do
  [[ -e "$path" ]] || { echo "probe path does not exist: $path" >&2; exit 3; }
  inode=$(stat -c '%i' "$path")
  safe=$(printf '%s' "$path" | sed 's#[^A-Za-z0-9_.-]#_#g')
  tsdbfs "$FS" inode "$inode" >"$OUT/oracle/tsdbfs-${inode}-${safe}.txt"
  mmgetlocation -f "$path" -Y -L >"$OUT/oracle/mmgetlocation-${inode}-${safe}.txt"
  printf '%s\t%s\n' "$inode" "$path" >>"$OUT/oracle/probes.tsv"
  probe_count=$((probe_count + 1))
done
[[ $probe_count -gt 0 ]] || { echo "at least one probe path is required" >&2; exit 3; }

# Cross-check only physical addresses from the inode-address line and the Disk
# pointers section. A blind [0-9]+:[0-9]+ scan also matches timestamps such as
# 17:56 and would turn clock text into bogus mmfileid requests.
extract_tsdbfs_addresses() {
  awk '
    FNR == 1 { inPointers=0 }
    /Inode address:/ { print; next }
    /Disk pointers \[/ { inPointers=1; next }
    inPointers && /trailer:/ { inPointers=0; next }
    inPointers { print }
  ' "$OUT"/oracle/tsdbfs-*.txt | grep -oE '[0-9]+:[0-9]+' | sort -u
}

# Cross-check every physical inode/data-pointer sector while the cluster is still
# mounted. The heading is part of the lab transcript because ordinary mmfileid
# output does not repeat the GPFS disk id. A failed lookup makes the capture
# ineligible rather than silently producing a partial oracle.
: >"$OUT/oracle/mmfileid.txt"
mmfileid_queries=0
mmfileid_failed=0
while IFS=: read -r disk sector; do
  [[ $disk =~ ^[0-9]+$ && $sector =~ ^[0-9]+$ ]] || continue
  mmfileid_queries=$((mmfileid_queries + 1))
  printf '===== %s:%s =====\n' "$disk" "$sector" >>"$OUT/oracle/mmfileid.txt"
  if ! mmfileid "$FS" -d ":${disk}:${sector}" >>"$OUT/oracle/mmfileid.txt" 2>&1; then
    printf 'mmfileid_status=failed\n' >>"$OUT/oracle/mmfileid.txt"
    mmfileid_failed=1
  fi
done < <(extract_tsdbfs_addresses)
(( mmfileid_queries > 0 )) || { echo "tsdbfs produced no physical addresses for mmfileid cross-checking" >&2; exit 5; }
(( mmfileid_failed == 0 )) || { echo "one or more mmfileid oracle lookups failed; see $OUT/oracle/mmfileid.txt" >&2; exit 5; }

uid=$(awk '$1 == "--uid" { print $2; exit }' "$OUT/oracle/mmlsfs-uid.txt")
format_version=$(awk '$1 == "-V" { print $2; exit }' "$OUT/oracle/mmlsfs-version.txt")
storage_version=$(awk '/GPFS version is/ { print $4; exit }' "$OUT/oracle/mmfsckx.txt")
[[ -n $uid && -n $format_version && -n $storage_version ]] || {
  echo "could not derive filesystem/version identity from IBM tool output" >&2
  exit 5
}

printf 'meta\tcorpus-id\t%s\n' "$CORPUS_ID" >>"$manifest"
printf 'meta\tcapture-id\t%s\n' "$CAPTURE_ID" >>"$manifest"
printf 'meta\toperation\t%s\n' "$OPERATION" >>"$manifest"
printf 'meta\tstorage-scale-version\t%s\n' "$storage_version" >>"$manifest"
printf 'meta\tformat-version\t%s\n' "$format_version" >>"$manifest"
printf 'meta\tfilesystem-uid\t%s\n' "$uid" >>"$manifest"
printf 'meta\tcapture-state\tunmounted-clean\n' >>"$manifest"

# Human -L/-m output is used only to join names to disk IDs and local devices;
# the complete parseable -Y output is archived above as the authoritative oracle.
declare -A disk_id sector_size device
while read -r name id sector; do
  disk_id["$name"]=$id
  sector_size["$name"]=$sector
done < <(awk '$2 == "nsd" && $9 ~ /^[0-9]+$/ { print $1, $9, $3 }' "$OUT/oracle/mmlsdisk-human.txt")
while read -r name dev; do
  [[ -n ${device[$name]:-} ]] || device["$name"]=$dev
done < <(awk '$3 ~ /^\/dev\// { print $1, $3 }' "$OUT/oracle/mmlsnsd-human.txt")

[[ ${#disk_id[@]} -ge 2 ]] || { echo "refusing capture: fewer than two NSDs were discovered" >&2; exit 6; }

sync
mmumount "$FS" -a
remount=1
if findmnt -rn -T "$MOUNT" -t gpfs >/dev/null 2>&1; then
  echo "refusing raw capture: $FS is still mounted" >&2
  exit 4
fi

for name in "${!disk_id[@]}"; do
  dev=${device[$name]:-}
  [[ -n $dev && -b $dev ]] || { echo "no local block device mapping for NSD $name" >&2; exit 6; }
  size=$(blockdev --getsize64 "$dev")
  tmp="$OUT/raw/${name}.partial.img"
  # conv=sparse changes only host-file allocation: reading the image still yields
  # every byte from the NSD, so the digest is over the complete logical raw disk.
  dd if="$dev" of="$tmp" bs=4M iflag=fullblock conv=sparse status=none
  [[ $(stat -c '%s' "$tmp") -eq $size ]] || { echo "short raw capture for NSD $name" >&2; exit 7; }
  hash=$(sha256sum "$tmp" | awk '{print $1}')
  image="$OUT/raw/${name}.${hash}.img"
  mv "$tmp" "$image"
  printf 'nsd\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$name" "${disk_id[$name]}" "$size" "${sector_size[$name]}" "$hash" "raw/$(basename "$image")" >>"$manifest"
done

add_artifacts() {
  local kind=$1 pattern=$2 file hash
  shopt -s nullglob
  for file in $pattern; do
    hash=$(sha256sum "$file" | awk '{print $1}')
    printf 'artifact\t%s\t%s\t%s\n' "$kind" "$hash" "${file#"$OUT/"}" >>"$manifest"
  done
  shopt -u nullglob
}
add_artifacts mmfsckx "$OUT/oracle/mmfsckx.txt"
add_artifacts tsdbfs "$OUT/oracle/tsdbfs-*.txt"
add_artifacts mmfileid "$OUT/oracle/mmfileid.txt"
add_artifacts mmgetlocation "$OUT/oracle/mmgetlocation-*.txt"
add_artifacts mmlsdisk "$OUT/oracle/mmlsdisk.txt"
add_artifacts mmlsnsd "$OUT/oracle/mmlsnsd.txt"

(
  cd "$OUT"
  find oracle raw -type f -print0 | sort -z | xargs -0 sha256sum >SHA256SUMS
  sha256sum manifest.tsv >>SHA256SUMS
)

mmmount "$FS" -a
remount=0
trap - EXIT
printf 'captured %s (%s) to %s\n' "$CAPTURE_ID" "$OPERATION" "$OUT"
