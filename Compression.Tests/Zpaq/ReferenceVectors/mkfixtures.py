"""Builds the zpaq 7.15 reference archives for Compression.Tests/Zpaq/ReferenceVectors.

Usage: python -I mkfixtures.py <zpaq.exe> <workdir> <outdir>
The inputs are generated deterministically here; only the archives and a manifest
(name, size, SHA-256 per version) are checked in.
"""
import hashlib, os, shutil, subprocess, sys

zpaq, work, out = sys.argv[1], sys.argv[2], sys.argv[3]

class Lcg:
    def __init__(self, seed): self.s = seed & 0xffffffff
    def next(self):
        self.s = (self.s * 1103515245 + 12345) & 0xffffffff
        return self.s >> 16

WORDS = ("the of and to in is that for it as with was on be by at this which from or an are "
         "archive journal fragment block version compress model context mixer predictor stream "
         "zpaq level method index header data file directory size date hash checksum").split()

def text(n, seed):
    r = Lcg(seed); b = bytearray()
    while len(b) < n:
        w = WORDS[r.next() % len(WORDS)]
        b += w.encode()
        k = r.next() % 16
        b += b'.\n' if k == 0 else (b', ' if k == 1 else b' ')
    return bytes(b[:n])

def rand(n, seed):
    r = Lcg(seed); return bytes(r.next() & 255 for _ in range(n))

def x86(n, seed):
    r = Lcg(seed); b = bytearray()
    while len(b) < n:
        k = r.next() % 8
        if k == 0: b += bytes([0xE8, r.next() & 255, r.next() & 15, 0, 0])
        elif k == 1: b += bytes([0x8B, 0x45 + (r.next() & 7) * 8, r.next() & 0x7C])
        elif k == 2: b += bytes([0xE9, r.next() & 255, 0xFF, 0xFF, 0xFF])
        else: b += bytes([0x89, 0x44, 0x24, (r.next() & 15) * 4])
    return bytes(b[:n])

def table(n):
    return bytes(((i * 7) ^ (i >> 4)) & 255 if i % 4 else (i >> 2) & 255 for i in range(n))

def write(root, name, data):
    p = os.path.join(root, name)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, 'wb') as f: f.write(data)

def run(*args, cwd):
    r = subprocess.run([zpaq, *args], cwd=cwd, capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit(f"zpaq {' '.join(args)} failed:\n{r.stdout}\n{r.stderr}")

def manifest_of(root):
    rows = []
    for dp, dns, fns in os.walk(root):
        for d in dns:
            rel = os.path.relpath(os.path.join(dp, d), os.path.dirname(root)).replace('\\', '/') + '/'
            rows.append((rel, -1, '-'))
        for f in fns:
            p = os.path.join(dp, f)
            rel = os.path.relpath(p, os.path.dirname(root)).replace('\\', '/')
            data = open(p, 'rb').read()
            rows.append((rel, len(data), hashlib.sha256(data).hexdigest()))
    return sorted(rows)

shutil.rmtree(work, ignore_errors=True); os.makedirs(work); os.makedirs(out, exist_ok=True)
lines = ["# archive\tversion\tname\tsize\tsha256 (size -1 = directory)"]

# One file set, every predefined method.
s = os.path.join(work, 'set')
t = text(20000, 1)
write(s, 'text.txt', t)
write(s, 'dup.txt', t)                 # deduplicated against text.txt
write(s, 'x86.bin', x86(16384, 2))
write(s, 'random.bin', rand(4096, 3))
write(s, 'empty.dat', b'')
write(s, 'sub/table.bin', table(16384))
setrows = manifest_of(s)
for m in range(6):
    name = f'set.m{m}.zpaq'
    run('a', name, 'set', '-method', str(m), cwd=work)
    shutil.copy(os.path.join(work, name), out)
    lines += [f"{name}\t1\t{n}\t{z}\t{h}" for n, z, h in setrows]

# Three versions: add, modify + add, delete.
v = os.path.join(work, 'v')
write(v, 'notes.txt', text(6000, 4))
write(v, 'a.bin', rand(3000, 5))
lines += [f"versions.zpaq\t1\t{n}\t{z}\t{h}" for n, z, h in manifest_of(v)]
run('a', 'versions.zpaq', 'v', cwd=work)
write(v, 'a.bin', rand(3000, 6))
write(v, 'b.txt', text(5000, 7))
lines += [f"versions.zpaq\t2\t{n}\t{z}\t{h}" for n, z, h in manifest_of(v)]
run('a', 'versions.zpaq', 'v', cwd=work)
os.remove(os.path.join(v, 'notes.txt'))
lines += [f"versions.zpaq\t3\t{n}\t{z}\t{h}" for n, z, h in manifest_of(v)]
run('a', 'versions.zpaq', 'v', cwd=work)
shutil.copy(os.path.join(work, 'versions.zpaq'), out)

# Streaming layout (method s...): BWT + order-0/1 CM, one block per file.
st = os.path.join(work, 'st')
write(st, 'one.txt', text(9000, 8))
write(st, 'two.bin', x86(7000, 9))
run('a', 'stream.zpaq', 'st', '-method', 's4.3ci1', cwd=work)
shutil.copy(os.path.join(work, 'stream.zpaq'), out)
lines += [f"stream.zpaq\t1\t{n}\t{z}\t{h}" for n, z, h in manifest_of(st) if z >= 0]

with open(os.path.join(out, 'manifest.tsv'), 'w', newline='\n') as f:
    f.write('\n'.join(lines) + '\n')
print('ok')
