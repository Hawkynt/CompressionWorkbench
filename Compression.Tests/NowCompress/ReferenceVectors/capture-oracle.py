#!/usr/bin/env python3
"""Verify pinned Now Compress samples with native unar, without the C# decoder."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import urllib.request


def sha(data):
    return hashlib.sha256(data).hexdigest()


def fork_pairs(folder):
    pairs = {}
    for path in folder.rglob("*"):
        if not path.is_file():
            continue
        blob = path.read_bytes()
        if path.name.startswith("._") and blob[:4] == b"\x00\x05\x16\x07":
            pair = pairs.setdefault(path.with_name(path.name[2:]), [b"", b""])
            for index in range(struct.unpack_from(">H", blob, 24)[0]):
                ident, offset, length = struct.unpack_from(">III", blob, 26 + 12 * index)
                if ident == 2:
                    pair[1] = blob[offset:offset + length]
                    if len(pair[1]) != length:
                        raise ValueError("Truncated AppleDouble resource fork")
        else:
            pairs.setdefault(path, [b"", b""])[0] = blob
    return list(pairs.values())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unar", required=True, help="Pinned native unar executable")
    parser.add_argument("--samples", required=True, type=Path)
    parser.add_argument("--download", action="store_true",
                        help="Download missing inputs from the recorded sample URLs")
    args = parser.parse_args()
    facts = json.loads(Path(__file__).with_name("native-oracle-facts.json")
                       .read_text(encoding="utf-8"))
    version = subprocess.check_output([args.unar, "-v"], text=True).strip()
    if not version.startswith("v1.10.7"):
        raise ValueError(f"Expected unar 1.10.7, got {version}")
    args.samples.mkdir(parents=True, exist_ok=True)
    total = 0
    for sample in facts["samples"]:
        path = args.samples / sample["fileName"]
        if not path.exists() and args.download:
            with urllib.request.urlopen(sample["url"], timeout=60) as response:
                blob = response.read()
            if sha(blob) != sample["archiveSha256"]:
                raise ValueError(f"Downloaded sample changed: {path.name}")
            path.write_bytes(blob)
        blob = path.read_bytes()
        if len(blob) != sample["archiveSize"] or sha(blob) != sample["archiveSha256"]:
            raise ValueError(f"Sample identity mismatch: {path.name}")
        with tempfile.TemporaryDirectory(prefix="nowcompress-oracle-") as folder:
            subprocess.run([args.unar, "-q", "-D", "-f", "-k", "hidden",
                            "-o", folder, str(path.resolve())], check=True)
            pairs = fork_pairs(Path(folder))
            lines = sorted(f"{len(data)}:{sha(data)}:{len(resource)}:{sha(resource)}"
                           for data, resource in pairs)
            fingerprint = sha("\n".join(lines).encode("ascii"))
            if (len(pairs) != sample["fileCount"]
                    or fingerprint != sample["forkPairsSha256"]):
                raise ValueError(f"Native decoder result changed: {path.name}")
            total += len(pairs)
            print(f"{path.name}: {len(pairs)} fork pairs match")
    print(f"All {len(facts['samples'])} archives and {total} files match.")


if __name__ == "__main__":
    main()
