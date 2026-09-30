"""Proves a published console SFX stub extracts a real archive on the machine it was built for.

usage: sfx-smoke.py <stub-path> <tier>

The stub is wrapped exactly the way SfxBuilder wraps it — stub, payload, then the twelve-byte
trailer [int64 little-endian payload offset]["SFX!"] — and executed. Every extracted file must be
byte-identical to its source.

Python's standard library can write zip, tar, tar.gz and tar.xz, which is what this checks. The
carved stubs for 7z, RAR, CAB and tar.zst are built by the matrix but not executed here; their
extraction is covered by the round-trip tests, which do have writers for them.
"""

import io
import os
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile
import zipfile

FILES = {
    "readme.txt": b"hello from a self-extracting archive\n",
    "nested/data.bin": bytes(range(256)) * 16,
    "nested/deeper/empty.txt": b"",
}


def build_payload(kind: str) -> bytes:
    buffer = io.BytesIO()
    if kind == "zip":
        with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, data in FILES.items():
                archive.writestr(name, data)
    else:
        mode = {"tar": "w", "tar.gz": "w:gz", "tar.xz": "w:xz"}[kind]
        with tarfile.open(fileobj=buffer, mode=mode) as archive:
            for name, data in FILES.items():
                info = tarfile.TarInfo(name)
                info.size = len(data)
                archive.addfile(info, io.BytesIO(data))
    return buffer.getvalue()


def run(stub: str, kind: str) -> None:
    with tempfile.TemporaryDirectory() as work:
        sfx = os.path.join(work, "archive" + (".exe" if stub.endswith(".exe") else ""))
        stub_bytes = open(stub, "rb").read()
        with open(sfx, "wb") as out:
            out.write(stub_bytes)
            out.write(build_payload(kind))
            out.write(struct.pack("<q", len(stub_bytes)))
            out.write(b"SFX!")
        os.chmod(sfx, os.stat(sfx).st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

        target = os.path.join(work, "out")
        result = subprocess.run([sfx, target], capture_output=True, text=True, timeout=300)
        print(f"[{kind}] exit {result.returncode}\n{result.stdout}{result.stderr}")
        if result.returncode != 0:
            sys.exit(f"[{kind}] the stub failed")

        for name, expected in FILES.items():
            path = os.path.join(target, *name.split("/"))
            if not os.path.isfile(path):
                sys.exit(f"[{kind}] missing after extraction: {name}")
            if open(path, "rb").read() != expected:
                sys.exit(f"[{kind}] content differs: {name}")
        print(f"[{kind}] all {len(FILES)} files byte-identical")


def main() -> None:
    stub, tier = sys.argv[1], sys.argv[2]
    kinds = {
        # The universal stub has to detect the format rather than assume it, so it gets two.
        "Universal": ["zip", "tar.gz"],
        "Zip": ["zip"],
        "Tar": ["tar"],
        "TarGz": ["tar.gz"],
        "TarXz": ["tar.xz"],
    }.get(tier)
    if kinds is None:
        print(f"{tier}: no standard-library writer for this format; built, not executed")
        return
    for kind in kinds:
        run(stub, kind)


if __name__ == "__main__":
    main()
