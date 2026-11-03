"""Records what sfextract 0.0.8 extracts from every installer in test-corpus/setupfactory.

Usage: python mkmanifest.py <dir holding the sfextract package> <corpus> <out.tsv>
       (pip install --target <dir> sfextract==0.0.8)
Each row: SHA-256 of the installer, entry path ('/'-separated), size and SHA-256 of the extracted
file. sfextract reads Setup Factory 7 to 9. Its version 7 reader always skips one byte after the
signature; for a version 7 installer that has no such byte it is given a copy with a zero byte
inserted there, which changes nothing else it reads. An installer it does not read gets one row
with the entry "!".
"""
import hashlib, os, sys, tempfile

sys.path.insert(0, sys.argv[1])
from sfextract.main import extract  # noqa: E402

corpus, out = sys.argv[2], sys.argv[3]
SIGNATURE7 = bytes([0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7])


def run(path, work):
    target = '\\\\?\\' + os.path.abspath(os.path.join(work, 'x')) if os.name == 'nt' else os.path.join(work, 'x')
    try:
        extractor = extract(path, target)
    except Exception as e:  # sfextract raises plain Exceptions
        return None, f'{type(e).__name__}'
    return (target, extractor) if extractor is not None else (None, 'no extractor')


rows = ["# installer-sha256\tentry\tsize\tsha256 -- what sfextract 0.0.8 extracts"]
for name in sorted(os.listdir(corpus)):
    path = os.path.join(corpus, name)
    if not os.path.isfile(path):
        continue
    data = open(path, 'rb').read()
    digest = hashlib.sha256(data).hexdigest()
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as work:
        target, info = run(path, work)
        at = data.find(SIGNATURE7)
        if target is None and at > 0 and data[at + 8:at + 16] != SIGNATURE7[:8] and data[at + 12:at + 14] == b'\x4a\x5d':
            padded = os.path.join(work, 'padded.exe')
            open(padded, 'wb').write(data[:at + 8] + b'\0' + data[at + 8:])
            target, info = run(padded, work)
        if target is None:
            rows.append(f"{digest}\t!\t0\tsfextract: {info}")
            continue
        for root, dirs, files in os.walk(target):
            for f in sorted(files):
                full = os.path.join(root, f)
                content = open(full, 'rb').read()
                rel = os.path.relpath(full, target).replace(os.sep, '/')
                rows.append(f"{digest}\t{rel}\t{len(content)}\t{hashlib.sha256(content).hexdigest()}")
with open(out, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(rows) + '\n')
