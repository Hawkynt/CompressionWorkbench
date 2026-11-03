"""Builds the KGB Archiver reference archives with Debian's kgb (1.0b4+ds).

Usage (Linux or WSL, kgb installed): python3 -I mkfixtures.py <workdir> <outdir>
The inputs are generated deterministically; manifest.tsv lists, per archive, every file as kgb
itself extracts it (name, size, SHA-256).
"""
import hashlib, os, random, shutil, struct, subprocess, sys

work, out = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
shutil.rmtree(work, ignore_errors=True)
src = os.path.join(work, 'src')
os.makedirs(os.path.join(src, 'sub'))
r = random.Random(20261103)
words = b"the quick brown fox jumps over a lazy dog while archivers model every bit".split()
files = {
    'text.txt': b"\r\n".join(b" ".join(r.choice(words) for _ in range(r.randint(4, 12))).capitalize() + b"." for _ in range(400)),
    'random.bin': bytes(r.getrandbits(8) for _ in range(3000)),
    'table.bin': b"".join(struct.pack("<IHH", 5000 + 13 * i, i % 5, 0xBEEF) for i in range(600)),
    'sub/code.exe': b"".join((b"\xE8" + struct.pack("<i", r.randrange(-3000, 3000)) if r.randrange(4) == 0 else bytes([0x8B, 0x45, r.randrange(16)])) for _ in range(1200)),
    'wave.raw': b"".join(struct.pack("<h", int(8000 * ((i % 50) - 25) / 25)) for i in range(2000)),
    'empty.dat': b'',
}
for name, data in files.items():
    with open(os.path.join(src, name), 'wb') as f:
        f.write(data)

rows = ["# archive\tname\tsize\tsha256 -- what kgb 1.0b4+ds extracts"]
for level in range(0, 7):
    archive = f'kgb-m{level}.kgb'
    target = os.path.join(work, archive)
    names = sorted(files)
    p = subprocess.run(['kgb', f'-{level}', target, *names], cwd=src, capture_output=True, text=True)
    if p.returncode != 0:
        raise SystemExit(p.stdout + p.stderr)
    x = os.path.join(work, 'x' + str(level))
    os.makedirs(os.path.join(x, 'sub'))
    p = subprocess.run(['kgb', target], cwd=x, capture_output=True, text=True)
    if p.returncode != 0:
        raise SystemExit(p.stdout + p.stderr)
    for name in names:
        data = open(os.path.join(x, name), 'rb').read()
        rows.append(f"{archive}\t{name}\t{len(data)}\t{hashlib.sha256(data).hexdigest()}")
    shutil.copy(target, out)
    print(archive, os.path.getsize(target))
with open(os.path.join(out, 'manifest.tsv'), 'w', newline='\n') as f:
    f.write('\n'.join(rows) + '\n')
