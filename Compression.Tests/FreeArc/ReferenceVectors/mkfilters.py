"""Builds the FreeArc 0.67 archives that exercise the rep, delta and exe stages.

Usage: python -I mkfilters.py <Arc.exe> <workdir> <outdir>
The inputs are generated deterministically; the archives and filters.tsv (name, size, SHA-256
of each file as `Arc.exe x` extracts it) are checked in.
"""
import hashlib, os, random, shutil, struct, subprocess, sys

arc, work, out = sys.argv[1], os.path.abspath(sys.argv[2]), os.path.abspath(sys.argv[3])
shutil.rmtree(work, ignore_errors=True)
src = os.path.join(work, 'f')
os.makedirs(src)
r = random.Random(20261103)


def write(name, data):
    with open(os.path.join(src, name), 'wb') as f:
        f.write(data)


words = b"alpha beta gamma delta epsilon zeta eta theta iota kappa lambda".split()
write('text.txt', b" ".join(r.choice(words) for _ in range(6000)))
block = bytes(r.getrandbits(8) for _ in range(24000))
# The same 24000 random bytes twice, 3000 bytes apart: only a long-range match finds the repeat.
write('repeats.bin', block + bytes(r.getrandbits(8) for _ in range(3000)) + block)
# A table of 8-byte records: a counter rising by 7, a cycling small value, a constant.
write('table.bin', b"".join(struct.pack("<IHH", 1000 + 7 * i, i % 3, 0xBEEF) for i in range(6000)))
code = bytearray()
while len(code) < 20000:
    if r.randrange(5) == 0:
        code += b"\xE8" + struct.pack("<i", r.randrange(-4000, 4000))
    else:
        code += bytes([0x8B, 0x45, r.randrange(256)])
write('code.exe', bytes(code))
write('empty.bin', b'')

rows = ["# archive\tname\tsize\tsha256 -- produced by FreeArc 0.67 Arc.exe x"]
for name, switch in [('filters-rep.arc', '-mrep:1mb'), ('filters-delta.arc', '-mdelta'),
                     ('filters-exe.arc', '-mexe'), ('filters-mx.arc', '-mx'),
                     ('filters-4x4.arc', '-m4x4:b64kb:lzma'), ('filters-m5x.arc', '-m5x')]:
    target = os.path.join(work, name)
    p = subprocess.run([arc, 'a', target, '-i0', '-cfg-', '-ep1', switch, os.path.join(src, '*')],
                       capture_output=True, text=True, errors='replace')
    if p.returncode != 0:
        raise SystemExit(f"{name}: {p.stdout}\n{p.stderr}")
    x = os.path.join(work, 'x-' + name)
    p = subprocess.run([arc, 'x', target, '-i0', '-cfg-', '-dp' + x], capture_output=True, text=True, errors='replace')
    if p.returncode != 0:
        raise SystemExit(f"x {name}: {p.stdout}\n{p.stderr}")
    for f in sorted(os.listdir(x)):
        data = open(os.path.join(x, f), 'rb').read()
        rows.append(f"{name}\t{f}\t{len(data)}\t{hashlib.sha256(data).hexdigest()}")
    shutil.copy(target, out)
    q = subprocess.run([arc, 'lt', target, '-i0', '-cfg-'], capture_output=True, text=True, errors='replace')
    print(name, os.path.getsize(target), [l.strip() for l in q.stdout.splitlines() if l.strip()[:1].isdigit()])

with open(os.path.join(out, 'filters.tsv'), 'w', newline='\n') as f:
    f.write('\n'.join(rows) + '\n')
print('ok')
