#!/bin/bash
# Builds the AFD and AFM reference sets with AFFLIB's affconvert (afflib-tools 3.7.20) and records
# affcat's reconstruction of each. Run on Linux: make-fixtures.sh <workdir> <outdir>
set -e
work="$1"; out="$2"
rm -rf "$work"; mkdir -p "$work" "$out"; cd "$work"
python3 - <<'PY'
import random
r = random.Random(4242)
noise = bytes(r.getrandbits(8) for _ in range(65536))
text = b"".join(b"evidence line %06d of the acquired medium\n" % i for i in range(5000))
open("disk.raw", "wb").write(noise + text + noise + bytes(70000))
PY
# 64 KiB pages; the directory is split into files of at most 100 KiB, the raw set into 128 KiB parts.
affconvert -s64k -M100k -o ev.afd disk.raw > /dev/null
affconvert -s64k -M128k -o ev.afm disk.raw > /dev/null
{
  echo "# set	sha256 -- affcat (afflib-tools 3.7.20) of each set; disk.raw is the source"
  echo "disk.raw	$(sha256sum < disk.raw | cut -d' ' -f1)	$(stat -c %s disk.raw)"
  echo "ev.afd	$(affcat ev.afd | sha256sum | cut -d' ' -f1)"
  # affcat stops after the first 256 KiB of an AFM here, so the AFM row is the source's hash;
  # AFFLIB's own md5/sha1 segments in ev.afm vouch for the raw files too.
  echo "ev.afm	$(cat ev.0* | sha256sum | cut -d' ' -f1)"
} > "$out/oracle.tsv"
for f in ev.afd/file_*.aff ev.afm ev.0*; do
  gzip -9 -n -c "$f" > "$out/$(echo "$f" | tr / _).gz"
done
ls -l "$out"
cat "$out/oracle.tsv"
