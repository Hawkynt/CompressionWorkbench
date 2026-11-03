"""Deterministic inputs for the MPQ oracle fixtures (no OS randomness)."""
import math
import os
import struct
import sys

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
words = ("the quick brown fox jumps over the lazy dog while warcraft peons gather "
         "gold and lumber near the town hall").split()
state = 12345


def rnd():
    global state
    state = (state * 1103515245 + 12345) & 0x7FFFFFFF
    return state


text = []
for i in range(2600):
    text.append(words[rnd() % len(words)])
    if i % 13 == 12:
        text.append("\r\n")
open(os.path.join(out, "text.txt"), "wb").write(" ".join(text).encode())

records = bytearray()
for i in range(700):
    records += struct.pack("<IHHf", i, rnd() & 0xFF, 0x1234, i * 0.5)
open(os.path.join(out, "records.bin"), "wb").write(bytes(records))

sparse = bytearray(9000)
for i in range(0, 9000, 700):
    sparse[i:i + 5] = b"\x01\x02\x03\x04\x05"
open(os.path.join(out, "sparse.bin"), "wb").write(bytes(sparse))


def wav(path, channels, frames):
    data = bytearray()
    for i in range(frames):
        for c in range(channels):
            v = int(9000 * math.sin(2 * math.pi * 440 * (c + 1) * i / 22050) + 3000 * math.sin(i / 7.0))
            data += struct.pack("<h", v)
    header = (b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt "
              + struct.pack("<IHHIIHH", 16, 1, channels, 22050, 22050 * 2 * channels, 2 * channels, 16)
              + b"data" + struct.pack("<I", len(data)))
    open(path, "wb").write(header + data)


wav(os.path.join(out, "mono.wav"), 1, 6000)
wav(os.path.join(out, "stereo.wav"), 2, 5000)
open(os.path.join(out, "tiny.txt"), "wb").write(b"MPQ")
open(os.path.join(out, "empty.bin"), "wb").write(b"")
