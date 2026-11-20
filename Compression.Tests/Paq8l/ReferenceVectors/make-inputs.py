"""Writes the deterministic inputs the paq8l reference archives were made from.

usage: python3 -I make-inputs.py <output dir>

Every input is generated here (no third-party content), chosen to wake each part of the paq8l
model: text (word, record and column models), x86-like code with CALL/JMP runs (EXE block and
transform), a 24-bit BMP (image model), a baseline JPEG made by Pillow from a generated picture
(JPEG block and model), a two-level fax-width bitmap, binary records, a tiny and an empty file.
"""
import os, random, struct, sys, zlib

out = sys.argv[1]
os.makedirs(os.path.join(out, 'docs'), exist_ok=True)
rng = random.Random(8)

words = ('the of and to in is was that for it with as his on be at by had this not but from or have an they which '
         'one you were her all she there would their we him been has when who will more no if out so said what up its '
         'about into than them can only other new some could time these two may then do first any my now such like our '
         'over man me even most made after also did many before must through back years where much your way well down '
         'should because each just those people how too little state good very make world still own see men work long '
         'archive model context mixing predictor arithmetic coder weight neural network bit history state map').split()

def text(n):
    s, line = [], []
    while sum(map(len, s)) < n:
        w = rng.choice(words)
        if not line: w = w.capitalize()
        line.append(w)
        if len(' '.join(line)) > 60 or rng.random() < 0.02:
            s.append(' '.join(line) + ('.' if rng.random() < 0.5 else ',') + '\n')
            line = []
    return ''.join(s).encode()[:n]

with open(os.path.join(out, 'text.txt'), 'wb') as f: f.write(text(24000))
with open(os.path.join(out, 'docs', 'notes.txt'), 'wb') as f: f.write(text(3000))

# x86-like code: filler instructions and CALLs (E8) / JMPs (E9) to a handful of fixed targets
code = bytearray()
targets = [0x400, 0x1234, 0x2000, 0x2468, 0x3a00, 0x4f10]
while len(code) < 20000:
    r = rng.random()
    if r < 0.25:
        op = 0xE8 if rng.random() < 0.8 else 0xE9
        t = rng.choice(targets)
        rel = t - (len(code) + 5)
        code += bytes([op]) + struct.pack('<i', rel)
    elif r < 0.5:
        code += bytes([0x8B, rng.choice([0x45, 0x4D, 0x55]), rng.randrange(0, 64, 4)])
    elif r < 0.7:
        code += bytes([0x89, 0x45, rng.randrange(0, 64, 4)])
    elif r < 0.8:
        code += bytes([0x83, 0xC4, rng.randrange(0, 32, 4)])
    elif r < 0.9:
        code += bytes([0x50 + rng.randrange(8)])
    else:
        code += bytes([0xC3, 0x90, 0x90])
with open(os.path.join(out, 'code.bin'), 'wb') as f: f.write(b'MZ' + bytes(62) + bytes(code))

# 24-bit BMP, 64 x 40, smooth gradients with a little noise
w, h = 64, 40
rowsize = (w * 3 + 3) & ~3
pixels = bytearray()
for y in range(h):
    row = bytearray()
    for x in range(w):
        row += bytes([(x * 4 + rng.randrange(3)) & 255, (y * 6 + rng.randrange(3)) & 255, ((x + y) * 3) & 255])
    pixels += row + bytes(rowsize - len(row))
bmp = b'BM' + struct.pack('<IHHI', 54 + len(pixels), 0, 0, 54) + struct.pack('<IiiHHIIiiII', 40, w, h, 1, 24, 0, len(pixels), 2835, 2835, 0, 0) + pixels
with open(os.path.join(out, 'image.bmp'), 'wb') as f: f.write(bmp)

# baseline JPEG from a generated picture
from PIL import Image
img = Image.new('RGB', (96, 64))
for y in range(64):
    for x in range(96):
        img.putpixel((x, y), ((x * 2 + y) & 255, (y * 4) & 255, (128 + x - y) & 255))
img.save(os.path.join(out, 'photo.jpg'), 'JPEG', quality=85, optimize=False, progressive=False)

# two-level bitmap rows of 216 bytes (1728 pixels), 40 rows
fax = bytearray()
for y in range(40):
    fax += bytes((0xFF if (x // 8 + y // 4) % 5 == 0 else 0x00) for x in range(216))
with open(os.path.join(out, 'fax.bin'), 'wb') as f: f.write(bytes(fax))

# fixed-length binary records
rec = bytearray()
for i in range(600):
    rec += struct.pack('<IHH8s', 1000 + i * 7, i % 13, (i * 37) & 0xFFFF, ('item%04d' % i).encode())
with open(os.path.join(out, 'records.bin'), 'wb') as f: f.write(bytes(rec))

with open(os.path.join(out, 'one.txt'), 'wb') as f: f.write(b'x')
with open(os.path.join(out, 'empty.txt'), 'wb') as f: f.write(b'')
