"""Deterministic inputs for the UHARC test fixtures (our own data, no third-party content)."""
import math, os, random, struct, sys, time

root = sys.argv[1]
STAMP = time.mktime((2005, 10, 1, 12, 0, 0, 0, 0, -1))

def put(rel, data):
    p = os.path.join(root, rel)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "wb") as f:
        f.write(data)

def words(rng, n, vocab):
    out, size = [], 0
    while size < n:
        w = rng.choice(vocab) + ("\r\n" if rng.random() < 0.1 else " ")
        out.append(w); size += len(w)
    return "".join(out)[:n].encode()

VOCAB = "the quick brown fox jumps over lazy dog lorem ipsum dolor sit amet".split()

# basic: every ordinary mode
rng = random.Random(1)
put("basic/hello.txt", b"Hello, UHARC!\r\n")
put("basic/empty.txt", b"")
put("basic/sub/nested.txt", b"nested file\r\n")
put("basic/text.txt", words(rng, 6000, VOCAB))
put("basic/zeros.bin", bytes(3000))
put("basic/random.bin", bytes(rng.getrandbits(8) for _ in range(700)))
os.makedirs(os.path.join(root, "basic", "emptydir"), exist_ok=True)

# reset: > 128 KiB so both LZP (64 KiB) and ALZ (128 KiB) reset their models, small files
# exercise the LZP merge rule
rng = random.Random(2)
small = "aa bb cc dd ee ff gg hh".split()
def records(n):
    out = bytearray()
    i = 0
    while len(out) < n:
        out += ("record %06d status %s" % (i, "ok" if rng.random() < 0.9 else "fail")).encode() + b"\r\n"
        i += 1
    return bytes(out[:n])
for i, n in enumerate([70000, 300, 5000, 120, 60000, 17000, 16000, 15000, 100, 9000, 1, 0, 2, 3000, 40000, 900, 800, 700]):
    put(f"reset/f{i:02}.txt", records(n))

# multimedia: data the ALZ selectors and the LZP delta filter pick up
rng = random.Random(3)
def wav(bits, ch, n, step):
    frames = bytearray()
    for i in range(n):
        for c in range(ch):
            v = math.sin(i * (step + 0.002 * c)) * 0.7
            frames += struct.pack("<B", int(128 + v * 120)) if bits == 8 else struct.pack("<h", int(v * 30000))
    ba = ch * bits // 8
    fmt = struct.pack("<HHIIHH", 1, ch, 11025, 11025 * ba, ba, bits)
    return b"RIFF" + struct.pack("<I", 36 + len(frames)) + b"WAVEfmt " + struct.pack("<I", 16) + fmt + b"data" + struct.pack("<I", len(frames)) + bytes(frames)
put("mm/u8.wav", wav(8, 1, 6000, 0.013))
put("mm/s16.wav", wav(16, 2, 3000, 0.011))
put("mm/m16.wav", wav(16, 1, 4000, 0.017))
w, h = 48, 40
row = (w * 3 + 3) & ~3
pix = bytearray()
for y in range(h):
    r = bytearray()
    for x in range(w):
        r += bytes([(x * 5) & 255, (y * 6) & 255, ((x + y) * 3) & 255])
    pix += r + bytes(row - len(r))
put("mm/pic.bmp", b"BM" + struct.pack("<IHHI", 54 + len(pix), 0, 0, 54) + struct.pack("<IiiHHIIiiII", 40, w, h, 1, 24, 0, len(pix), 2835, 2835, 0, 0) + bytes(pix))
put("mm/s8.raw", bytes((int(math.sin(i * 0.02) * 100) & 0xFF) for i in range(5000)))
put("mm/be16.raw", b"".join(struct.pack(">h", int(math.sin(i * 0.015) * 20000)) for i in range(3000)))
put("mm/rgba.raw", b"".join(bytes([(i * 3) & 255, (i // 7) & 255, (i * 5 // 3) & 255, 255]) for i in range(2500)))
put("mm/gray.raw", bytes((int(128 + 60 * math.sin(i * 0.01) + 40 * math.sin((i // 64) * 0.2)) + rng.randint(-2, 2)) & 255 for i in range(3000)))
put("mm/w16.raw", b"".join(struct.pack("<H", (i * 7 + rng.randint(0, 3)) & 0xFFFF) for i in range(1500)))
put("mm/rgb565.raw", b"".join(struct.pack("<H", (((i % 64) >> 1) << 11) | (((i // 64) % 64) << 5) | ((i // 3) % 32)) for i in range(1500)))
put("mm/s8n.raw", bytes(int(math.sin(i * 0.02) * 90 + rng.uniform(-6, 6)) & 255 for i in range(3000)))
put("mm/u8s.wav", wav(8, 2, 1500, 0.013))
put("mm/ramp.raw", bytes(((i // 3) + int(math.sin(i * 0.05) * 4)) & 0xFF for i in range(5000)))

# executables: synthetic x86 images (own bytes) for the E8/E9 and relocation filter
rng = random.Random(4)
def code(n, base):
    out = bytearray()
    while len(out) < n:
        r = rng.random()
        if r < 0.15:
            out += b"\xE8" + struct.pack("<i", rng.randint(-len(out) - base, 4000))
        elif r < 0.2:
            out += b"\xE9" + struct.pack("<i", rng.choice([rng.randint(-200, 200), rng.randint(0x10000, 0x7FFFFF)]))
        else:
            out += bytes(rng.choice([b"\x55", b"\x8B\xEC", b"\x89\x45\xFC", b"\x33\xC0", b"\xC3", b"\x90", b"\x83\xC4\x08"]))
    return bytes(out[:n])
relocs = 0x20
mz = bytearray(0x40)
mz[0:2] = b"MZ"
struct.pack_into("<H", mz, 6, relocs)
struct.pack_into("<H", mz, 0x18, 0x40)
table = b"".join(struct.pack("<HH", 0x10 + 6 * i, 0x100 + (i // 8)) for i in range(relocs))
put("exe/dosprog.exe", bytes(mz) + table + code(6000, 0x40 + len(table)))
elf = bytearray(0x34)
elf[0:4] = b"\x7FELF"
elf[0x12] = 3
put("exe/linux.bin", bytes(elf) + code(3000, 0x34))
put("exe/tiny.com", b"MZ" + bytes(0x3E) + b"\xE8\x10\x00\x00\x00" * 40)

for dirpath, dirs, files in os.walk(root):
    for f in files + dirs:
        os.utime(os.path.join(dirpath, f), (STAMP, STAMP))
