# Deterministic inputs for the RAR 4 reference vectors (written by rar 6.24 -ma4).
import math, os, random, struct, sys

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
rnd = random.Random(20261026)

words = ("the archive stores each file with its own header and a packed block that "
         "continues the dictionary of the file before it when the archive is solid "
         "filters transform executable code audio samples and pictures before the "
         "match finder sees them so that similar values line up").split()
text = []
for line in range(900):
    text.append(" ".join(rnd.choice(words) for _ in range(rnd.randint(6, 14))).capitalize() + ".")
open(os.path.join(out, "text.txt"), "w", newline="\n").write("\n".join(text) + "\n")

# x86-like code: filler opcodes with frequent near calls/jumps to a few targets.
code = bytearray()
targets = [rnd.randrange(0, 60000) for _ in range(40)]
while len(code) < 64 * 1024:
    if rnd.random() < 0.18:
        op = 0xE8 if rnd.random() < 0.7 else 0xE9
        rel = rnd.choice(targets) - (len(code) + 5)
        code += bytes([op]) + struct.pack("<i", rel)
    else:
        code += bytes([rnd.choice([0x55, 0x89, 0xE5, 0x8B, 0x45, 0x08, 0x83, 0xEC, 0x10, 0xC3, 0x90, 0x5D])])
open(os.path.join(out, "x86.bin"), "wb").write(bytes(code[:64 * 1024]))

# Four interleaved slowly rising 8-bit channels (delta filter food).
table = bytearray()
acc = [0, 50, 100, 150]
for i in range(8192):
    for c in range(4):
        acc[c] = (acc[c] + rnd.randint(0, 3) + c) & 0xFF
        table.append(acc[c])
open(os.path.join(out, "table.bin"), "wb").write(bytes(table))

# 16-bit stereo WAV, two sines plus a little noise.
n = 16000
pcm = bytearray()
for i in range(n):
    l = int(12000 * math.sin(i * 0.05) + rnd.randint(-40, 40))
    r = int(9000 * math.sin(i * 0.031 + 1) + rnd.randint(-40, 40))
    pcm += struct.pack("<hh", l, r)
wav = b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 2, 44100, 44100 * 4, 4, 16) + b"data" + struct.pack("<I", len(pcm)) + pcm
open(os.path.join(out, "audio.wav"), "wb").write(wav)

# 24-bit BMP, 96 x 64 smooth gradients.
w, h = 96, 64
rowsize = (w * 3 + 3) & ~3
pix = bytearray()
for y in range(h):
    row = bytearray()
    for x in range(w):
        row += bytes([(x * 2 + y) & 0xFF, (x + y * 3) & 0xFF, (128 + x - y) & 0xFF])
    row += b"\0" * (rowsize - w * 3)
    pix += row
bmp = b"BM" + struct.pack("<IHHI", 54 + len(pix), 0, 0, 54) + struct.pack("<IiiHHIIiiII", 40, w, h, 1, 24, 0, len(pix), 2835, 2835, 0, 0) + pix
open(os.path.join(out, "image.bmp"), "wb").write(bmp)

# IA-64-like bundles: templates with branch slots and opcode-5 instructions.
ia = bytearray()
for b in range(2048):
    bundle = bytearray(rnd.randbytes(16))
    bundle[0] = (bundle[0] & 0xE0) | rnd.choice([0x10, 0x11, 0x12, 0x16, 0x18, 0x1C])
    v = int.from_bytes(bundle, "little")
    for slot in range(3):
        start = slot * 41 + 5
        v &= ~(0xF << (start + 37))
        v |= 5 << (start + 37)
    ia += v.to_bytes(16, "little")
open(os.path.join(out, "ia64.bin"), "wb").write(bytes(ia))
print("ok")
