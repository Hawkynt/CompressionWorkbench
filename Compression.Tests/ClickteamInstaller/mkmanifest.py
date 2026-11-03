"""Records what cicdec 3.0.1 extracts from every installer in test-corpus/clickteam.

Usage (Windows, cicdec.exe from https://github.com/Bioruebe/cicdec/releases/tag/3.0.1):
    python -I mkmanifest.py <cicdec.exe> <corpus> <out.tsv>
Each row: SHA-256 of the installer, entry path ('/'-separated), size and SHA-256 of the extracted
file. An installer cicdec does not extract completely ("All OK") gets one row with the entry "!".
"""
import hashlib, os, subprocess, sys, tempfile

cicdec, corpus, out = sys.argv[1], sys.argv[2], sys.argv[3]
rows = ["# installer-sha256\tentry\tsize\tsha256 -- what cicdec 3.0.1 extracts"]
for name in sorted(os.listdir(corpus)):
    path = os.path.join(corpus, name)
    if not os.path.isfile(path):
        continue
    digest = hashlib.sha256(open(path, 'rb').read()).hexdigest()
    with tempfile.TemporaryDirectory() as work:
        target = os.path.join(work, 'x')
        p = subprocess.run([cicdec, path, target], capture_output=True, text=True, errors='replace')
        lines = p.stdout.strip().splitlines()
        if p.returncode != 0 or not lines or lines[-1].strip() != 'All OK':
            reason = (lines[-1] if lines else 'failed').replace('\t', ' ')
            rows.append(f"{digest}\t!\t0\t{reason}")
            continue
        for root, dirs, files in os.walk(target):
            for f in files:
                full = os.path.join(root, f)
                data = open(full, 'rb').read()
                rel = os.path.relpath(full, target).replace(os.sep, '/')
                rows.append(f"{digest}\t{rel}\t{len(data)}\t{hashlib.sha256(data).hexdigest()}")
with open(out, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(rows) + '\n')
print(len(rows) - 1, 'rows')
