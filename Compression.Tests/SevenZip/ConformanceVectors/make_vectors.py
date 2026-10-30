"""Regenerates the 7z conformance vectors with the real 7-Zip.

usage: python make_vectors.py <path to 7z.exe or 7zz>

Writes every <profile>.7z next to this script and manifest.tsv: one line per entry,
archive, name, kind (file / dir / anti), size and SHA-256 (empty for dir and anti).
The source tree is derived deterministically below, so only the archives and the
manifest are checked in.
"""
import hashlib
import os
import random
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
PASSWORD = "S3cret!"

PROFILES = [
    ("bcj", ["-mf=BCJ"]),
    ("bcj2", ["-mf=BCJ2"]),
    ("bcj2-explicit", ["-m0=BCJ2", "-m1=LZMA2:d=1m", "-m2=LZMA:d=64k", "-m3=LZMA:d=64k", "-mb0:1", "-mb0s1:2", "-mb0s2:3"]),
    ("ia64", ["-mf=IA64"]),
    ("sparc", ["-mf=SPARC"]),
    ("delta4", ["-mf=Delta:4"]),
    ("swap4", ["-m0=Swap4", "-m1=LZMA2"]),
    ("lzma2-mx1", ["-m0=LZMA2", "-mx1"]),
    ("hc-off", ["-mhc=off"]),
    ("solid-2f", ["-ms=2f"]),
    ("times-all", ["-mtc=on", "-mta=on"]),
    ("aes", ["-p" + PASSWORD]),
    ("aes-bcj2", ["-p" + PASSWORD, "-mf=BCJ2"]),
    ("aes-mhe", ["-p" + PASSWORD, "-mhe=on"]),
]


def words(rng, size):
    vocab = [b"alpha ", b"beta ", b"gamma ", b"delta ", b"lorem ", b"ipsum\n"]
    out = bytearray()
    while len(out) < size:
        out += rng.choice(vocab)
    return bytes(out[:size])


def x86_like(rng, size):
    out = bytearray()
    while len(out) < size:
        if rng.randrange(4) == 0 and len(out) + 5 <= size:
            out.append(0xE8 if rng.randrange(2) == 0 else 0xE9)
            out += (rng.randrange(-4000, 4000) & 0xFFFFFFFF).to_bytes(4, "little")
        elif rng.randrange(16) == 0 and len(out) + 6 <= size:
            out += bytes([0x0F, 0x80 | rng.randrange(16)])
            out += (rng.randrange(-4000, 4000) & 0xFFFFFFFF).to_bytes(4, "little")
        else:
            out.append(rng.randrange(0x40, 0x60))
    return bytes(out[:size])


def tree(rng):
    return {
        "text.txt": words(rng, 6000),
        "code/x86.bin": x86_like(rng, 9000),
        "code/words.bin": bytes(rng.getrandbits(8) for _ in range(4096)),
        "empty.txt": b"",
        "emptydir": None,
        "deep/er/still.txt": words(rng, 700),
        "ünïcødé_日本語.txt": words(rng, 900),
    }


def materialize(root, files):
    for name, data in files.items():
        path = os.path.join(root, *name.split("/"))
        if data is None:
            os.makedirs(path, exist_ok=True)
        else:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as f:
                f.write(data)


def listing(root):
    entries = {}
    for dirpath, dirnames, filenames in os.walk(root):
        for d in dirnames:
            rel = os.path.relpath(os.path.join(dirpath, d), root).replace(os.sep, "/")
            entries[rel] = None
        for f in filenames:
            rel = os.path.relpath(os.path.join(dirpath, f), root).replace(os.sep, "/")
            with open(os.path.join(dirpath, f), "rb") as fh:
                entries[rel] = fh.read()
    return entries


def run(seven_zip, cwd, args):
    subprocess.run([seven_zip, *args], cwd=cwd, check=True, stdout=subprocess.DEVNULL)


def main():
    seven_zip = sys.argv[1]
    work = tempfile.mkdtemp()
    try:
        src = os.path.join(work, "src")
        materialize(src, tree(random.Random(20261030)))
        expected = listing(src)
        manifest = []
        for name, switches in PROFILES:
            archive = os.path.join(HERE, name + ".7z")
            if os.path.exists(archive):
                os.remove(archive)
            run(seven_zip, src, ["a", "-y", "-r", archive, *switches, "*"])
            for entry, data in sorted(expected.items()):
                manifest.append(entry_line(name, entry, data))

        # An archive with nothing in it: add a file, then delete it.
        empty = os.path.join(HERE, "empty.7z")
        if os.path.exists(empty):
            os.remove(empty)
        run(seven_zip, src, ["a", "-y", empty, "text.txt"])
        run(seven_zip, src, ["d", "-y", empty, "text.txt"])

        # A differential update archive: anti-items for what vanished, the new file in full.
        upd = os.path.join(work, "upd")
        materialize(upd, {"keep.txt": b"keep", "gone.txt": b"gone", "gonedir": None})
        base = os.path.join(work, "base.7z")
        run(seven_zip, upd, ["a", "-y", "-r", base, "*"])
        os.remove(os.path.join(upd, "gone.txt"))
        os.rmdir(os.path.join(upd, "gonedir"))
        with open(os.path.join(upd, "new.txt"), "wb") as f:
            f.write(b"new")
        anti = os.path.join(HERE, "anti.7z")
        if os.path.exists(anti):
            os.remove(anti)
        run(seven_zip, upd, ["u", "-y", "-r", base, "-u-", "-up0q3r2x2y2z0w2!" + anti, "*"])
        manifest.append("anti.7z\tgone.txt\tanti\t0\t")
        manifest.append("anti.7z\tgonedir\tanti\t0\t")
        manifest.append(entry_line("anti", "new.txt", b"new"))

        with open(os.path.join(HERE, "manifest.tsv"), "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(manifest) + "\n")
    finally:
        shutil.rmtree(work, ignore_errors=True)


def entry_line(archive, entry, data):
    if data is None:
        return f"{archive}.7z\t{entry}\tdir\t0\t"
    return f"{archive}.7z\t{entry}\tfile\t{len(data)}\t{hashlib.sha256(data).hexdigest()}"


if __name__ == "__main__":
    main()
