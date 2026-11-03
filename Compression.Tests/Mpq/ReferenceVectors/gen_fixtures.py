"""Builds the StormLib-written MPQ fixtures and prints StormLib's own verification of them.

usage: python -I gen_fixtures.py <mpqoracle.exe> <inputs dir> <output dir>
"""
import hashlib
import os
import subprocess
import sys

tool, inputs, out = sys.argv[1:4]
os.makedirs(out, exist_ok=True)


def spec(local, archived, flags, mask):
    return f"{os.path.join(inputs, local)}={archived}:{flags:X}:{mask}"


COMPRESS, IMPLODE, ENCRYPTED, KEY_V2 = 0x200, 0x100, 0x10000, 0x20000
SINGLE, SECTOR_CRC = 0x01000000, 0x04000000

archives = {
    "stormlib-v1.mpq": ("v1a", [
        spec("text.txt", "war3map.j", COMPRESS | ENCRYPTED | KEY_V2, "08"),
        spec("records.bin", "Units\\records.bin", COMPRESS | ENCRYPTED, "02"),
        spec("records.bin", "bz\\records.bin", COMPRESS | SECTOR_CRC, "10"),
        spec("text.txt", "Scripts\\implode.txt", IMPLODE | ENCRYPTED, "0"),
        spec("mono.wav", "Sound\\mono.wav", COMPRESS | ENCRYPTED, "w0"),
        spec("mono.wav", "Sound\\mono-adpcm.wav", COMPRESS | ENCRYPTED, "w1"),
        spec("stereo.wav", "Sound\\stereo.wav", COMPRESS, "w2"),
        spec("stereo.wav", "Sound\\stereo-mid.wav", COMPRESS, "w1"),
        spec("text.txt", "single.txt", COMPRESS | SINGLE, "02"),
        spec("text.txt", "single-enc.txt", COMPRESS | SINGLE | ENCRYPTED | KEY_V2, "08"),
        spec("tiny.txt", "tiny.txt", COMPRESS, "08"),
        spec("empty.bin", "empty.bin", COMPRESS, "02"),
        spec("sparse.bin", "sparse.bin", COMPRESS, "20"),
        spec("text.txt", "plain.txt", 0, "0"),
    ]),
    "stormlib-v2.mpq": ("v2", [
        spec("text.txt", "lzma.txt", COMPRESS, "12"),
        spec("sparse.bin", "sparse-zlib.bin", COMPRESS | ENCRYPTED, "22"),
        spec("sparse.bin", "sparse-bzip2.bin", COMPRESS, "30"),
        spec("records.bin", "records.bin", COMPRESS | SECTOR_CRC, "02"),
        spec("text.txt", "pkware.txt", COMPRESS, "08"),
    ]),
}

for name, (version, entries) in archives.items():
    path = os.path.join(out, name)
    subprocess.run([tool, "create", path, version, *entries], check=True)
    verify = subprocess.run([tool, "verify", path], capture_output=True, text=True)
    print(f"== {name} verify rc={verify.returncode}\n{verify.stdout}{verify.stderr}")
    with open(path, "rb") as f:
        print(name, hashlib.sha256(f.read()).hexdigest())
