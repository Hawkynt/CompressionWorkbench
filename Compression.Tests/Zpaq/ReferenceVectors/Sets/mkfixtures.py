"""Builds the zpaq 7.15 encrypted, multi-part and index reference archives.

Usage: python -I mkfixtures.py <zpaq64.exe> <workdir> <outdir>
Inputs are generated deterministically; the archives and manifest.tsv (per archive set and
version: name, size, SHA-256 as `zpaq x -until N` extracts them) are checked in.
"""
import hashlib, os, shutil, subprocess, sys

zpaq, work, out = sys.argv[1], os.path.abspath(sys.argv[2]), os.path.abspath(sys.argv[3])
PASSWORD = "pässwört 1"  # non-ASCII: zpaq hashes the UTF-8 bytes of -key


class Lcg:
    def __init__(self, seed): self.s = seed & 0xffffffff
    def next(self):
        self.s = (self.s * 1103515245 + 12345) & 0xffffffff
        return self.s >> 16


def text(n, seed):
    words = "the archive journal fragment block version compress model index part key salt".split()
    r = Lcg(seed); b = bytearray()
    while len(b) < n:
        b += words[r.next() % len(words)].encode() + (b'.\n' if r.next() % 9 == 0 else b' ')
    return bytes(b[:n])


def rand(n, seed):
    r = Lcg(seed); return bytes(r.next() & 255 for _ in range(n))


def write(root, name, data, day=1):
    p = os.path.join(root, name)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, 'wb') as f: f.write(data)
    t = 1_600_000_000 + day * 86400  # a distinct date per version, so zpaq sees the change
    os.utime(p, (t, t))


def run(*args):
    r = subprocess.run([zpaq, *args], cwd=work, capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit(f"zpaq {' '.join(args)} failed:\n{r.stdout}\n{r.stderr}")
    return r.stdout


def extracted(archive, version, *extra):
    target = os.path.join(work, 'x')
    shutil.rmtree(target, ignore_errors=True)
    run('x', archive, '-until', str(version), '-to', 'x', *extra)
    rows = []
    for dp, dns, fns in os.walk(target):
        for f in fns:
            p = os.path.join(dp, f)
            data = open(p, 'rb').read()
            rows.append((os.path.relpath(p, target).replace('\\', '/'), len(data), hashlib.sha256(data).hexdigest()))
    return sorted(rows)


shutil.rmtree(work, ignore_errors=True); os.makedirs(work); os.makedirs(out, exist_ok=True)
lines = ["# set\tversion\tname\tsize\tsha256"]
v = os.path.join(work, 'v')


def stage(n):
    """Version n of the input tree: grows, changes and loses files from one version to the next."""
    if n == 1:
        write(v, 'notes.txt', text(7000, 1))
        write(v, 'data/a.bin', rand(5000, 2))
    elif n == 2:
        write(v, 'data/a.bin', rand(5000, 3), 2)
        write(v, 'data/b.txt', text(9000, 4), 2)
    elif n == 3:
        os.remove(os.path.join(v, 'notes.txt'))
        write(v, 'c.txt', text(3000, 5) + text(3000, 5), 3)


def build(set_name, archive, versions, *extra, until_extra=()):
    shutil.rmtree(v, ignore_errors=True)
    rows = []
    for n in range(1, versions + 1):
        stage(n)
        run('a', archive, 'v', *extra)
        rows += [f"{set_name}\t{n}\t{name}\t{size}\t{sha}" for name, size, sha in extracted(archive, n, *until_extra)]
    return rows


# Encrypted single archive, two versions.
lines += build('enc.zpaq', 'enc.zpaq', 2, '-key', PASSWORD, until_extra=('-key', PASSWORD))
# Multi-part: every update is a new part (part001.zpaq, part002.zpaq, part003.zpaq).
lines += build('part???.zpaq', 'part???', 3)
# Encrypted multi-part: the salt is in part 1; later parts continue its key stream.
lines += build('ep???.zpaq', 'ep???', 2, '-key', PASSWORD, until_extra=('-key', PASSWORD))
# Index: ix000.zpaq holds the transaction headers, fragment tables and file indexes; the data is
# in ix001.zpaq and ix002.zpaq, each a suffix written against the index.
lines += build('ix???.zpaq', 'ix???', 2, '-index', 'ix000.zpaq')
# Two unrelated archives whose names look like parts of one set.
shutil.rmtree(v, ignore_errors=True); stage(1)
run('a', 'dec1.zpaq', 'v')
lines += [f"dec1.zpaq\t1\t{n}\t{z}\t{h}" for n, z, h in extracted('dec1.zpaq', 1)]
shutil.rmtree(v, ignore_errors=True); stage(1); stage(2)
run('a', 'dec2.zpaq', 'v')
lines += [f"dec2.zpaq\t1\t{n}\t{z}\t{h}" for n, z, h in extracted('dec2.zpaq', 1)]

for f in sorted(os.listdir(work)):
    if f.endswith('.zpaq'):
        shutil.copy(os.path.join(work, f), out)
with open(os.path.join(out, 'manifest.tsv'), 'w', newline='\n') as f:
    f.write('\n'.join(lines) + '\n')
print(run('l', 'ix000.zpaq'))
print('ok')
