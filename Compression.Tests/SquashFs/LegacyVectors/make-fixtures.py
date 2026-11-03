#!/usr/bin/env python3
"""Builds the SquashFS 1.x/2.x/3.x reference images and their oracle listing.

Run on Linux as a user that may sudo (owners are set with chown):
    python3 make-fixtures.py <workdir> <squashfs-src-root>
<squashfs-src-root> holds squashfs1.3r3, squashfs2.2-r2, squashfs3.0 and squashfs3.4 from
https://sourceforge.net/projects/squashfs/files/squashfs/, each with squashfs-tools built
(see README.md). Every image is checked by unsquashfs 4.x and the oracle comes from its
extraction (run as root, so owners and modes are kept).
"""
import gzip, hashlib, os, random, shutil, subprocess, sys

work, src = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
payload = os.path.join(work, "payload")
if os.path.exists(work):
    subprocess.run(["sudo", "rm", "-rf", work], check=True)
os.makedirs(payload)

rng = random.Random(4711)
T = 1_200_000_000  # fixed mtime for every entry


def write(rel, data):
    p = os.path.join(payload, rel)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "wb") as f:
        f.write(data)


write("readme.txt", b"SquashFS legacy fixture.\n" * 3)
write("empty.bin", b"")
write("docs/lorem.txt", (b"Lorem ipsum dolor sit amet, consectetur adipiscing elit. " * 1500))  # ~85 KB: several blocks + tail
write("docs/random.bin", bytes(rng.getrandbits(8) for _ in range(9_000)))  # incompressible: a stored block
write("deep/a/b/c/leaf.txt", b"leaf\n")
os.makedirs(os.path.join(payload, "emptydir"))
# 300 entries with long names: more than one directory header (256 max) and, for 2.x/3.x, a
# directory large enough to be written as an extended directory with an index.
for i in range(300):
    write(f"many/file-with-a-rather-long-name-number-{i:04d}.txt", f"entry {i}\n".encode())
os.symlink("docs/lorem.txt", os.path.join(payload, "link-to-lorem"))
os.symlink("/absolute/target/that/does/not/exist", os.path.join(payload, "dangling"))
os.chmod(os.path.join(payload, "readme.txt"), 0o640)
os.chmod(os.path.join(payload, "deep/a/b/c/leaf.txt"), 0o600)
for root, dirs, files in os.walk(payload):
    for n in dirs + files:
        os.utime(os.path.join(root, n), (T, T), follow_symlinks=False)
os.utime(payload, (T, T))
subprocess.run(["sudo", "chown", "-h", "1234:4321", os.path.join(payload, "readme.txt")], check=True)
subprocess.run(["sudo", "chown", "-h", "0:0", os.path.join(payload, "docs/random.bin")], check=True)

tools = {
    "1.3": os.path.join(src, "squashfs1.3r3/squashfs-tools/mksquashfs"),
    "2.2": os.path.join(src, "squashfs2.2-r2/squashfs-tools/mksquashfs"),
    "3.0": os.path.join(src, "squashfs3.0/squashfs-tools/mksquashfs"),
    "3.4": os.path.join(src, "squashfs3.4/squashfs-tools/mksquashfs"),
}
variants = [
    ("v1.0-le.sqfs", "1.3", ["-le"]),
    ("v1.0-be.sqfs", "1.3", ["-be"]),
    ("v1.0-le-check-uncompressed.sqfs", "1.3", ["-le", "-check_data", "-noI", "-noD"]),
    ("v2.0-le.sqfs", "2.2", ["-le", "-2.0"]),
    ("v2.1-le.sqfs", "2.2", ["-le"]),
    ("v2.1-be.sqfs", "2.2", ["-be"]),
    ("v3.0-le.sqfs", "3.0", ["-le"]),
    ("v3.1-le.sqfs", "3.4", ["-le"]),
    ("v3.1-be.sqfs", "3.4", ["-be"]),
    ("v3.1-le-check-nofrag.sqfs", "3.4", ["-le", "-check_data", "-no-fragments", "-b", "4096"]),
    ("v3.1-le-uncompressed.sqfs", "3.4", ["-le", "-noI", "-noD", "-noF"]),
]

rows = ["# image\tpath\ttype\tsize\tmode\tuid\tgid\tmtime\tsha256-or-target"]
for name, tool, opts in variants:
    img = os.path.join(work, name)
    extra = ["-noappend"] + (["-no-progress"] if tool == "3.4" else [])
    subprocess.run(["sudo", tools[tool], payload, img] + extra + opts, check=True, stdout=subprocess.DEVNULL)
    subprocess.run(["sudo", "chown", f"{os.getuid()}:{os.getgid()}", img], check=True)
    out = os.path.join(work, "x-" + name)
    subprocess.run(["sudo", "unsquashfs", "-no-xattrs", "-q", "-d", out, img], check=True, stdout=subprocess.DEVNULL)
    entries = []
    for root, dirs, files in os.walk(out):
        for n in dirs + files:
            p = os.path.join(root, n)
            rel = os.path.relpath(p, out)
            st = os.lstat(p)
            if os.path.islink(p):
                kind, size, tail = "l", len(os.readlink(p)), os.readlink(p)
            elif os.path.isdir(p):
                kind, size, tail = "d", 0, "-"
            else:
                kind, size = "f", st.st_size
                content = subprocess.run(["sudo", "cat", p], check=True, capture_output=True).stdout
                tail = hashlib.sha256(content).hexdigest()
            entries.append(f"{name}\t{rel}\t{kind}\t{size}\t{st.st_mode & 0o7777:o}\t{st.st_uid}\t{st.st_gid}\t{int(st.st_mtime)}\t{tail}")
    rows += sorted(entries)
    with open(img, "rb") as f:
        data = f.read()
    with open(img + ".gz", "wb") as f:
        f.write(gzip.compress(data, compresslevel=9, mtime=0))
    print(name, len(data), "entries", len(entries))

with open(os.path.join(work, "oracle.tsv"), "w", newline="\n") as f:
    f.write("\n".join(rows) + "\n")
