"""Records what WiseUnpacker 3.0.0 extracts from every installer in test-corpus/wise.

Usage (Windows, WiseUnpacker.exe from https://github.com/mnadareski/WiseUnpacker/releases/tag/3.0.0):
    python -I mkmanifest.py <WiseUnpacker.exe> <corpus> <out.tsv>
Each row: SHA-256 of the installer, entry path ('/'-separated), size and SHA-256 of the extracted
file. Setups whose Wise stub sits inside a launcher are given to WiseUnpacker as that stub (the
first embedded PE image, up to the launcher's certificate), which is how it reads them. When
WiseUnpacker does not finish, the installer also gets a row with the entry "!" and the files it
did write before stopping are still recorded.
"""
import hashlib, os, struct, subprocess, sys, tempfile

unpacker, corpus, out = sys.argv[1], sys.argv[2], sys.argv[3]


def embedded_stub(data):
    i = 2
    while True:
        i = data.find(b'MZ', i)
        if i < 0:
            return None
        if i + 0x40 <= len(data):
            p = struct.unpack_from('<I', data, i + 0x3C)[0]
            if 0 < p < 0x1000 and data[i + p:i + p + 4] == b'PE\0\0':
                break
        i += 2
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    if data[pe:pe + 4] != b'PE\0\0':
        return None
    magic = struct.unpack_from('<H', data, pe + 24)[0]
    directories = pe + 24 + (96 if magic == 0x10B else 112)
    certificate = struct.unpack_from('<I', data, directories + 32)[0]
    return data[i:certificate or len(data)]


def run(path, target):
    p = subprocess.run([unpacker, '-x', '-o', target, path], capture_output=True, text=True, errors='replace')
    lines = [l for l in (p.stdout + p.stderr).splitlines() if l.strip()]
    ok = p.returncode == 0 and bool(lines) and lines[-1].startswith('Extracted ')
    return ok, 'WiseUnpacker did not finish'


rows = ["# installer-sha256\tentry\tsize\tsha256 -- what WiseUnpacker 3.0.0 extracts"]
for name in sorted(os.listdir(corpus)):
    path = os.path.join(corpus, name)
    if not os.path.isfile(path):
        continue
    data = open(path, 'rb').read()
    digest = hashlib.sha256(data).hexdigest()
    with tempfile.TemporaryDirectory() as work:
        target = os.path.join(work, 'x')
        ok, last = run(path, target)
        if not ok:
            stub = embedded_stub(data)
            if stub is not None:
                inner = os.path.join(work, 'stub.exe')
                open(inner, 'wb').write(stub)
                target = os.path.join(work, 'y')
                ok, last = run(inner, target)
        if not ok:
            rows.append(f"{digest}\t!\t0\t{last.replace(chr(9), ' ')}")
        for root, dirs, files in os.walk(target):
            for f in sorted(files):
                full = os.path.join(root, f)
                content = open(full, 'rb').read()
                rel = os.path.relpath(full, target).replace(os.sep, '/')
                rows.append(f"{digest}\t{rel}\t{len(content)}\t{hashlib.sha256(content).hexdigest()}")
with open(out, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(rows) + '\n')
