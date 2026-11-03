"""Builds the multi-volume ACE sets with ACE 2.6 (ACE32.EXE, the console archiver of WinAce).

Usage: python -I mkvolumes.py <ACE32.EXE> <workdir> <outdir>
The inputs are generated deterministically. ACE32.EXE runs on Windows; the expected contents in
volumes.tsv (set, name, size, SHA-256) are what `unace x` (unace 2.5 for Linux, run in WSL)
extracts from each set, so the sets are proven readable by the reference extractor too.
"""
import hashlib, os, random, shutil, subprocess, sys

ace, work, out = sys.argv[1], os.path.abspath(sys.argv[2]), os.path.abspath(sys.argv[3])
shutil.rmtree(work, ignore_errors=True)
src = os.path.join(work, 'src')
os.makedirs(os.path.join(src, 'sub'))
r = random.Random(20261103)
words = b"alpha beta gamma delta epsilon zeta eta theta iota kappa".split()
with open(os.path.join(src, 'text.txt'), 'wb') as f:
    f.write(b"\r\n".join(b" ".join(r.choice(words) for _ in range(10)) for _ in range(2500)))
with open(os.path.join(src, 'rand.bin'), 'wb') as f:
    f.write(bytes(r.getrandbits(8) for _ in range(80000)))
with open(os.path.join(src, 'sub', 'small.txt'), 'wb') as f:
    f.write(b"a short file after the split members\r\n")

rows = ["# set\tname\tsize\tsha256 -- produced by unace 2.5 x"]
for name, switches in [('vol-store', ['-m0']),
                       ('vol-solid', ['-m3', '-s', '-c2', '-d64']),
                       ('vol-ace1', ['-m3', '-s-', '-c2-', '-d32']),
                       ('vol-enc', ['-m3', '-s', '-c2', '-pSecret'])]:
    target = os.path.join(work, name + '.ace')
    p = subprocess.run([ace, 'a', '-y', '-cfg-', '-r', '-v65', *switches, target, '*'], cwd=src,
                       capture_output=True, text=True, errors='replace', input='\n' * 20, timeout=120)
    if p.returncode != 0:
        raise SystemExit(f"{name}: {p.stdout}\n{p.stderr}")
    volumes = sorted(f for f in os.listdir(work) if f.lower().startswith(name + '.'))
    x = os.path.join(work, 'x-' + name)
    os.makedirs(x)
    wsl_target = subprocess.run(['wsl', 'wslpath', '-a', target.replace('\\', '/')], capture_output=True, text=True).stdout.strip()
    wsl_x = subprocess.run(['wsl', 'wslpath', '-a', x.replace('\\', '/')], capture_output=True, text=True).stdout.strip()
    password = [s[2:] for s in switches if s.startswith('-p')]
    q = subprocess.run(['wsl', 'sh', '-c', f"cd '{wsl_x}' && unace x -y {'-p' + password[0] if password else ''} '{wsl_target}'"],
                       capture_output=True, text=True, errors='replace')
    if q.returncode != 0:
        raise SystemExit(f"unace {name}: {q.stdout}\n{q.stderr}")
    for root, _, files in os.walk(x):
        for f in sorted(files):
            path = os.path.join(root, f)
            data = open(path, 'rb').read()
            rel = os.path.relpath(path, x).replace(os.sep, '/')
            rows.append(f"{name}\t{rel}\t{len(data)}\t{hashlib.sha256(data).hexdigest()}")
    for v in volumes:
        shutil.copy(os.path.join(work, v), out)
    print(name, [(v, os.path.getsize(os.path.join(work, v))) for v in volumes])

with open(os.path.join(out, 'volumes.tsv'), 'w', newline='\n') as f:
    f.write('\n'.join(rows) + '\n')
print('ok')
