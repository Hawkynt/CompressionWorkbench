"""Rebuilds the SIS fixtures from a symdev checkout (https://github.com/4akloon/symdev, MIT).

usage: python make-fixtures.py <symdev checkout> <this directory>

Writes the packages its tests keep as hex (made by the SDK's makesis.exe / signsis.exe) as
binaries, plus oracle.tsv: the target path, size and SHA-256 of every input file the package was
built from, as symdev's own tests name them.
"""
import hashlib, os, sys

src, dst = sys.argv[1], sys.argv[2]
td = os.path.join(src, 'crates', 'symdev-sis', 'src', 'testdata')


def hexfile(name):
    return bytes.fromhex(''.join(open(os.path.join(td, name)).read().split()))


packages = {
    'hello.sis': 'hello_sis.hex',
    'hello.sisx': 'hello_sisx.hex',
    'hello_reg.sis': 'hello_reg_sis.hex',
    'gui.sis': 'exp51_gui_sis.hex',
}
for out, name in packages.items():
    open(os.path.join(dst, out), 'wb').write(hexfile(name))

hello_exe = hexfile('hello_type30.hex')[60:]  # symdev: hello_exe_bytes()
inputs = [
    ('hello.sis', 'sys/bin/hello.exe', hello_exe),
    ('hello.sisx', 'sys/bin/hello.exe', hello_exe),
    ('gui.sis', 'sys/bin/gui.exe', hexfile('exp51_gui_exe.hex')),
    ('gui.sis', 'resource/apps/gui.rsc', hexfile('exp51_gui_rsc.hex')),
    ('gui.sis', 'private/10003a3f/import/apps/gui_reg.rsc', hexfile('exp51_gui_reg_rsc.hex')),
]
rows = [f'{p}\t{n}\t{len(b)}\t{hashlib.sha256(b).hexdigest()}' for p, n, b in inputs]
open(os.path.join(dst, 'oracle.tsv'), 'w', newline='\n').write(
    '# package\tpath\tsize\tsha256 -- the input files makesis packed, from symdev testdata\n' + '\n'.join(rows) + '\n')
print('\n'.join(rows))
