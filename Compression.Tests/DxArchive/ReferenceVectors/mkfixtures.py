"""Builds the DxLib archive vectors with the archivers DxLib ships (Windows; documentation, not a build input).

Usage: python -I mkfixtures.py <DxaEncode-1.10.exe> <DxaEncode-1.06.exe> <workdir> <outdir>
  1.10 is DxLib 3.25a's Tool/DXArchive/DxaEncode.exe (writes version 8),
  1.06 is DxLib 3.19b's (writes version 6).
The inputs are generated deterministically; manifest.tsv lists every input file (path, size,
SHA-256), which every archive must extract to.
"""
import hashlib, os, random, shutil, subprocess, sys

enc8, enc6, work, out = (os.path.abspath(a) for a in sys.argv[1:5])
shutil.rmtree(work, ignore_errors=True)
src = os.path.join(work, 'Data')
os.makedirs(os.path.join(src, 'BasicData'))
os.makedirs(os.path.join(src, 'Picture', 'Chara'))
os.makedirs(os.path.join(src, 'Empty'))
r = random.Random(29)
files = {
    'Game.dat': b'wolf rpg editor data archive test line\r\n' * 150,
    'BasicData/Map001.mps': bytes(r.getrandbits(8) for _ in range(3000)),
    'BasicData/CommonEvent.dat': bytes((i * 7) & 0x3F for i in range(9000)),
    'Picture/Chara/hero.png': b'\x89PNG\r\n\x1a\n' + bytes(r.getrandbits(8) % 5 for _ in range(6000)),
    'Picture/title.bmp': b'BM' + bytes(range(256)) * 20,
    'empty.txt': b'',
    'Readme with spaces.txt': 'ウディタのデータ'.encode('cp932') * 20,
}
for name, data in files.items():
    with open(os.path.join(src, *name.split('/')), 'wb') as f:
        f.write(data)

def run(cmd, cwd=None):
    p = subprocess.run(cmd, cwd=cwd, capture_output=True, input=b'\n', timeout=300)
    if p.returncode != 0:
        raise SystemExit(f'{cmd}: {p.returncode} {p.stdout[-300:]!r}')

for name, opts in [('v8-default', []), ('v8-key', ['-K:Secret']), ('v8-nokey', ['-NK']), ('v8-stored', ['-NP']),
                   ('v8-huffall', ['-H:255', '-AH']), ('v8-huff1k', ['-H:1', '-AH']), ('v8-maxpress', ['-MP'])]:
    target = os.path.join(work, name + '.dxa')
    run([enc8, '-NS', *opts, src, target])
    shutil.copy(target, out)
    print(name, os.path.getsize(target))
for name, opts in [('v6-default', []), ('v6-key', ['-K:Secret'])]:
    produced = os.path.join(work, 'Data.dxa')
    if os.path.exists(produced):
        os.remove(produced)
    run([enc6, *opts, src])
    shutil.copy(produced, os.path.join(out, name + '.dxa'))
    print(name, os.path.getsize(produced))

rows = ['# path\tsize\tsha256 -- the files every archive holds']
for name, data in sorted(files.items()):
    rows.append(f'{name}\t{len(data)}\t{hashlib.sha256(data).hexdigest()}')
with open(os.path.join(out, 'manifest.tsv'), 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(rows) + '\n')
