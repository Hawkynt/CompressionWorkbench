"""Extracts each MPQ with StormLib and writes a manifest of name, size and SHA-256 per file.

usage: python -I manifest.py <mpqoracle.exe> <mpq> <work dir>   (manifest printed to stdout)
"""
import hashlib
import os
import shutil
import subprocess
import sys

tool, mpq, work = sys.argv[1:4]
shutil.rmtree(work, ignore_errors=True)
os.makedirs(work)
listing = subprocess.run([tool, "extract", mpq, work], capture_output=True, text=True)
if listing.returncode != 0:
    sys.exit(f"StormLib failed: rc={listing.returncode}\n{listing.stdout}{listing.stderr}")
rows = []
for line in listing.stdout.splitlines():
    name, size, flags, status = line.split("\t")
    data = open(os.path.join(work, name.replace("\\", "#").replace("/", "#")), "rb").read()
    rows.append((name, int(size), flags, hashlib.sha256(data).hexdigest()))
for name, size, flags, digest in sorted(rows):
    print(f"{name}\t{size}\t{flags}\t{digest}")
