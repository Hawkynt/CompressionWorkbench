import gzip, hashlib, os, shutil, sys, tempfile
# usage: oracleevb.py <evbpy dir> <srcdir> <dstdir> name.exe[:legacy] ...
#   gzips each packed exe into dstdir and records what evbunpack extracts (path, size, sha256) in oracle.tsv
sys.path.insert(0, sys.argv[1])
from evbunpack.__main__ import unpack_files  # noqa: E402

src, dst = sys.argv[2], sys.argv[3]
os.makedirs(dst, exist_ok=True)
rows = []
for spec in sys.argv[4:]:
    name, _, mode = spec.partition(':')
    path = os.path.join(src, name)
    open(os.path.join(dst, name + '.gz'), 'wb').write(gzip.compress(open(path, 'rb').read(), 9, mtime=0))
    out = tempfile.mkdtemp()
    unpack_files(path, out, legacy_fs=(mode == 'legacy'), fs_listing_only=False)
    for root, dirs, files in os.walk(out):
        for n in sorted(files):
            full = os.path.join(root, n)
            rel = os.path.relpath(full, out).replace('\\', '/')
            b = open(full, 'rb').read()
            rows.append(f'{name}\t{rel}\t{len(b)}\t{hashlib.sha256(b).hexdigest()}')
        for d in dirs:
            full = os.path.join(root, d)
            if not os.listdir(full):
                rows.append(f'{name}\t{os.path.relpath(full, out).replace(chr(92), "/")}/\t0\t-')
    shutil.rmtree(out, ignore_errors=True)
open(os.path.join(dst, 'oracle.tsv'), 'w', newline='\n').write(
    '# fixture\tpath\tsize\tsha256 -- produced by evbunpack 0.2.3 (unpack_files)\n' + '\n'.join(sorted(rows)) + '\n')
print('\n'.join(sorted(rows)))
