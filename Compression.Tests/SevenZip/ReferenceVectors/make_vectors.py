"""Regenerates the 7z coder reference vectors (documentation, not a build input).

usage: python make_vectors.py <7z.exe of 7-Zip 26.03> <7z.exe of 7-Zip-zstd 26.02 with its Codecs\\ plugins> <outdir>

Every archive holds one member, payload.bin (or big.bin for the -mt variants), whose bytes this script derives deterministically,
so the tests regenerate the expected payload instead of checking it in.
"""
import os
import random
import struct
import subprocess
import sys
import tempfile


def payload() -> bytes:
    """~78 KiB: English-ish text, then synthetic AArch64 words (BL + ADRP), then RISC-V words (JAL + AUIPC/JALR)
    so the branch filters actually rewrite something."""
    rnd = random.Random(0x7A)
    words = "the quick brown fox jumps over lazy dog archive codec filter frame block stream".split()
    text = " ".join(rnd.choice(words) for _ in range(12000)).encode()[:24 * 1024]
    arm = bytearray()
    for i in range(6 * 1024):
        k = rnd.randrange(4)
        if k == 0:
            arm += struct.pack("<I", 0x94000000 | rnd.randrange(1 << 26))           # BL imm26
        elif k == 1:
            arm += struct.pack("<I", 0x90000000 | rnd.randrange(1 << 24) << 5 | rnd.randrange(32))  # ADRP
        else:
            arm += struct.pack("<I", 0xD503201F)                                     # NOP
    rv = bytearray()
    for i in range(6 * 1024):
        k = rnd.randrange(4)
        if k == 0:
            rv += struct.pack("<I", 0x0000006F | (rnd.randrange(32) << 7) | (rnd.randrange(1 << 20) << 12))  # JAL
        elif k == 1:
            reg = rnd.randrange(1, 32)
            rv += struct.pack("<I", 0x00000017 | (reg << 7) | (rnd.randrange(1 << 20) << 12))  # AUIPC
            rv += struct.pack("<I", 0x00000067 | (1 << 7) | (reg << 15) | (rnd.randrange(1 << 12) << 20))  # JALR
        else:
            rv += struct.pack("<I", 0x00000013)                                       # NOP (addi x0,x0,0)
    return text + bytes(arm) + bytes(rv)


def big_payload() -> bytes:
    """6 MiB of low-entropy text: large enough that the multi-threaded 7-Zip-zstd coders split it into
    several frames, compressible enough that the archives stay small."""
    line = b"".join(b"%06d the multi-threaded coder splits its input into independent frames\n" % i for i in range(512))
    return (line * (6 * 1024 * 1024 // len(line) + 1))[:6 * 1024 * 1024]


def main() -> None:
    sz, szz, out = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(out, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        src = os.path.join(tmp, "payload.bin")
        with open(src, "wb") as f:
            f.write(payload())
        big = os.path.join(tmp, "big.bin")
        with open(big, "wb") as f:
            f.write(big_payload())
        jobs = [
            (sz, "deflate64.7z", ["-m0=Deflate64"], src),
            (sz, "arm64-lzma2.7z", ["-m0=ARM64", "-m1=LZMA2"], src),
            (sz, "riscv-lzma2.7z", ["-m0=RISCV", "-m1=LZMA2"], src),
            (szz, "zstd.7z", ["-m0=zstd", "-mmt=1"], src),
            (szz, "zstd-mt.7z", ["-m0=zstd", "-mmt=4"], big),
            (szz, "brotli.7z", ["-m0=brotli", "-mmt=1"], src),
            (szz, "brotli-mt.7z", ["-m0=brotli", "-mmt=4"], big),
            (szz, "lz4.7z", ["-m0=lz4", "-mmt=1"], src),
            (szz, "lz4-mt.7z", ["-m0=lz4", "-mmt=4"], big),
        ]
        for exe, name, args, member in jobs:
            dst = os.path.join(out, name)
            if os.path.exists(dst):
                os.remove(dst)
            subprocess.run([exe, "a", "-t7z", "-ms=off", "-mhc=off", *args, dst, member], check=True, capture_output=True)
            print(name, os.path.getsize(dst))
        # py7zr (1.1.0) writes Brotli as one plain stream, without 7-Zip-zstd's skippable-frame wrapper.
        import py7zr
        dst = os.path.join(out, "brotli-py7zr.7z")
        if os.path.exists(dst):
            os.remove(dst)
        with py7zr.SevenZipFile(dst, "w", filters=[{"id": py7zr.FILTER_BROTLI, "level": 11}]) as z:
            z.write(src, "payload.bin")
        print("brotli-py7zr.7z", os.path.getsize(dst))
        import hashlib
        with open(os.path.join(out, "manifest.txt"), "w", newline="\n") as m:
            for name in ("payload.bin", "big.bin"):
                with open(os.path.join(tmp, name), "rb") as f:
                    data = f.read()
                m.write(f"{name} {len(data)} {hashlib.sha256(data).hexdigest()}\n")


if __name__ == "__main__":
    main()
