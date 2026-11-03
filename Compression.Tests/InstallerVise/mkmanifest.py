"""Records what unvise extracts from every installer in test-corpus/installer-vise.

Usage (Linux or WSL, unvise built from timdoug/unvise): python3 -I mkmanifest.py <unvise> <corpus> <out.tsv>
Each row: SHA-256 of the installer file, entry name as the reader names it (the data fork under its
catalog path, the resource fork with ".rsrc" appended), size and SHA-256 of the extracted fork.
An installer unvise refuses gets one row with the entry "!" and unvise's message.
"""
import hashlib, os, subprocess, sys, tempfile

unvise, corpus, out = sys.argv[1], sys.argv[2], sys.argv[3]
rows = ["# installer-sha256\tentry\tsize\tsha256 -- what unvise extracts"]
for name in sorted(os.listdir(corpus)):
    path = os.path.join(corpus, name)
    if not os.path.isfile(path) or name.endswith('.data') and os.path.exists(path[:-5]):
        continue  # payload companions are read with their application
    digest = hashlib.sha256(open(path, 'rb').read()).hexdigest()
    with tempfile.TemporaryDirectory() as work:
        target = os.path.join(work, 'x')
        p = subprocess.run([unvise, '-x', target, path], capture_output=True, text=True, errors='replace')
        if p.returncode != 0:
            rows.append(f"{digest}\t!\t0\t{p.stderr.strip().splitlines()[-1] if p.stderr.strip() else 'failed'}")
            continue
        for root, dirs, files in os.walk(target):
            for f in files:
                full = os.path.join(root, f)
                rel = os.path.relpath(full, target).replace(os.sep, '/')
                if rel.endswith('.data'):
                    rel = rel[:-5]
                elif not rel.endswith('.rsrc'):
                    raise SystemExit('unexpected output name ' + rel)
                data = open(full, 'rb').read()
                rows.append(f"{digest}\t{rel}\t{len(data)}\t{hashlib.sha256(data).hexdigest()}")
with open(out, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(rows) + '\n')
print(len(rows) - 1, 'rows')
