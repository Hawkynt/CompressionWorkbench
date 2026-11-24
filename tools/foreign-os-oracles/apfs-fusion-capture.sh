#!/bin/bash
# APFS Fusion oracle capture for a disposable GitHub-hosted macOS runner (Apple hardware).
#
# Makes a Fusion container out of two raw disk images (a small main tier and a larger tier 2),
# adds a volume, writes more data than the main tier holds so APFS has to place blocks on tier 2,
# and records what macOS says: diskutil's view of the container, fsck_apfs on both devices, and
# the SHA-256 of every file. Both raw images and the records land in $1.
#
# Every 4096-byte block of a data file starts with "CWB <file> <block>" and is padded with one
# byte value, so the images compress to almost nothing and each block names itself.
set -uo pipefail
OUT=${1:?output directory}
MAIN_MB=${MAIN_MB:-320}
TIER2_MB=${TIER2_MB:-1024}
mkdir -p "$OUT"
cd "$OUT"
log() { echo "== $*" | tee -a capture.log; }

sw_vers | tee sw_vers.txt
mkfile -n "${MAIN_MB}m" main.img
mkfile -n "${TIER2_MB}m" tier2.img
MAIN=$(hdiutil attach -imagekey diskimage-class=CRawDiskImage -nomount main.img | awk 'NR==1{print $1}')
TIER2=$(hdiutil attach -imagekey diskimage-class=CRawDiskImage -nomount tier2.img | awk 'NR==1{print $1}')
log "main=$MAIN tier2=$TIER2"
diskutil info "$MAIN" >> capture.log 2>&1
diskutil info "$TIER2" >> capture.log 2>&1

created=0
for form in "-main $MAIN -secondary $TIER2" "-main $MAIN $TIER2" "$MAIN $TIER2"; do
  log "diskutil apfs createContainer $form"
  # shellcheck disable=SC2086
  if sudo diskutil apfs createContainer $form > create.log 2>&1; then created=1; cat create.log >> capture.log; break; fi
  cat create.log >> capture.log
done
if [ "$created" != 1 ]; then
  log "no Fusion container could be created from disk images"
  echo '{"fusion": false}' > result.json
  hdiutil detach "$MAIN" >/dev/null 2>&1; hdiutil detach "$TIER2" >/dev/null 2>&1
  exit 0
fi

diskutil apfs list -plist > apfs-list.plist
diskutil apfs list >> capture.log
CONTAINER=$(sed -n 's/.*Created new APFS Container \(disk[0-9]*\).*/\1/p' create.log | head -1)
FUSION=$(diskutil apfs list "$CONTAINER" | grep -i -E 'fusion|Physical Store' | tr '\n' ' ')
log "container=$CONTAINER fusion=$FUSION"
sudo diskutil apfs addVolume "$CONTAINER" APFS Fusion >> capture.log 2>&1
VOL=/Volumes/Fusion
[ -d "$VOL" ] || { log "volume did not mount"; exit 1; }

python3 - "$VOL" "$MAIN_MB" <<'PY'
import os, sys, hashlib, json
vol, main_mb = sys.argv[1], int(sys.argv[2])
def body(name, blocks, fill):
    out = bytearray()
    for b in range(blocks):
        head = f"CWB {name} {b}\n".encode()
        out += head + bytes([fill]) * (4096 - len(head))
    return bytes(out)
files = {}
def put(rel, data):
    path = os.path.join(vol, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)
        f.flush(); os.fsync(f.fileno())
    files[rel] = {"size": len(data), "sha256": hashlib.sha256(data).hexdigest()}
put("small.txt", b"Hello Fusion\n")
put("dir1/nested/deep.txt", b"a file two directories down\n")
for i in range(50):
    put(f"many/f{i:03d}.txt", f"file {i}\n".encode())
# Big files, together well past the main tier, so APFS must use tier 2.
total = int(main_mb * 1.8) * 256          # blocks of 4 KiB
per = 32768                                # 128 MiB each
n = 0
while total > 0:
    count = min(per, total)
    put(f"big/big{n:02d}.bin", body(f"big{n:02d}", count, 0x41 + n % 26))
    total -= count; n += 1
json.dump(files, open("manifest.json", "w"), indent=1, sort_keys=True)
print(len(files), "files")
PY
sync
df -k "$VOL" >> capture.log
diskutil apfs list >> capture.log
diskutil unmount "$VOL" >> capture.log 2>&1
sudo fsck_apfs -n "$MAIN" > fsck-main.txt 2>&1; echo "exit $?" >> fsck-main.txt
diskutil apfs list -plist > apfs-list-final.plist
hdiutil detach "$MAIN" >> capture.log 2>&1 || hdiutil detach -force "$MAIN" >> capture.log 2>&1
hdiutil detach "$TIER2" >> capture.log 2>&1 || hdiutil detach -force "$TIER2" >> capture.log 2>&1
echo "{\"fusion\": \"$FUSION\", \"container\": \"$CONTAINER\"}" > result.json
shasum -a 256 main.img tier2.img > images.sha256
cat images.sha256
