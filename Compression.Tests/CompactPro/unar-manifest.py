"""Extracts an archive with unar into a Linux temporary directory and prints one line per fork:
hex(UTF-8 path) TAB sha256. Resource forks, which unar writes as AppleDouble "name.rsrc", are
hashed on their fork payload. Hex paths keep names intact whatever the console's code page.

usage: python3 -I unar-manifest.py <archive>
"""
import hashlib, os, shutil, struct, subprocess, sys, tempfile

archive = sys.argv[1]
out = tempfile.mkdtemp()
try:
    r = subprocess.run(['unar', '-q', '-f', '-k', 'visible', '-D', '-o', out, archive], capture_output=True)
    if r.returncode != 0:
        sys.stderr.write(r.stderr.decode('utf-8', 'replace'))
        sys.exit(r.returncode)
    for root, _, files in os.walk(out):
        for f in files:
            full = os.path.join(root, f)
            data = open(full, 'rb').read()
            rel = os.path.relpath(full, out)
            if rel.endswith('.rsrc') and data[:4] == b'\x00\x05\x16\x07':
                count = struct.unpack('>H', data[24:26])[0]
                fork = b''
                for i in range(count):
                    eid, off, ln = struct.unpack('>III', data[26 + 12 * i:38 + 12 * i])
                    if eid == 2:
                        fork = data[off:off + ln]
                data = fork
            print(rel.encode('utf-8', 'surrogateescape').hex() + '\t' + hashlib.sha256(data).hexdigest())
finally:
    shutil.rmtree(out, ignore_errors=True)
