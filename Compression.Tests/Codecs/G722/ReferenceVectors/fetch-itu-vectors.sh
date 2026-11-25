#!/usr/bin/env bash
# Downloads the ITU-T G.722 Appendix II digital test sequences (little-endian binary set) from
# itu.int, checks every file against the CRC-32 and size the distribution's own read-me lists,
# and prints the export line for G722ItuTestSequenceTests.
# Usage: ./fetch-itu-vectors.sh [target-directory]   (default: ./itu-g722-ts-le)
# The sequences are ITU copyright; keep them out of the repository.
set -euo pipefail
TARGET="${1:-$(dirname "$0")/itu-g722-ts-le}"
PYTHON="${PYTHON:-python3}"
URL='https://www.itu.int/rec/dologin_pub.asp?lang=e&id=T-REC-G.722-201209-I!!SOFT-ZST-E&type=items'
ZIP="$(mktemp)"
trap 'rm -f "$ZIP"' EXIT

curl -fsSL -o "$ZIP" "$URL"
mkdir -p "$TARGET"
"$PYTHON" - "$ZIP" "$TARGET" <<'PY'
import os, sys, zipfile, zlib
expected = {
    "bt1c1.xmt": (0x0C3BFCA7, 32832), "bt1c2.xmt": (0x2D604685, 1600),
    "bt1d3.cod": (0x7398964F, 32832), "bt2r1.cod": (0xD1DAA1D1, 32832),
    "bt2r2.cod": (0x344EA5D0, 1600),  "bt3h1.rc0": (0xE9250851, 32832),
    "bt3h2.rc0": (0x5330AE2E, 1600),  "bt3h3.rc0": (0x3731AD7F, 32832),
    "bt3l1.rc1": (0xED1B3993, 32832), "bt3l1.rc2": (0x8E8C4E2B, 32832),
    "bt3l1.rc3": (0xB7AA5569, 32832), "bt3l2.rc1": (0xAF00F31F, 1600),
    "bt3l2.rc2": (0x9143E92C, 1600),  "bt3l2.rc3": (0xAE855C07, 1600),
    "bt3l3.rc1": (0xA5374659, 32832), "bt3l3.rc2": (0x687B250A, 32832),
    "bt3l3.rc3": (0x3605736B, 32832),
}
prefix = "Software/G.722_MB-testvectors/g722-ts-le/bin/"
with zipfile.ZipFile(sys.argv[1]) as archive:
    for name, (crc, size) in expected.items():
        data = archive.read(prefix + name)
        if len(data) != size or zlib.crc32(data) != crc:
            sys.exit(f"{name}: size {len(data)} crc {zlib.crc32(data):08X}, expected {size} {crc:08X}")
        with open(os.path.join(sys.argv[2], name), "wb") as f:
            f.write(data)
PY
echo "export CWB_G722_ITU_VECTORS=\"$(cd "$TARGET" && pwd)\""
