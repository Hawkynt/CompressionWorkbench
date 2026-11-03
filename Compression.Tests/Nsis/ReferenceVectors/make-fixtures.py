import os, random, subprocess, sys
W = sys.argv[1]          # work dir
NSIS3 = sys.argv[2]      # makensis 3 path
NSIS2 = sys.argv[3]      # makensis 2 path
os.makedirs(os.path.join(W, 'payload', 'sub'), exist_ok=True)
os.chdir(W)
open('payload/readme.txt', 'wb').write(b'Hello from an NSIS fixture.\r\nLine two.\r\n')
open('payload/sub/readme-copy.txt', 'wb').write(b'Hello from an NSIS fixture.\r\nLine two.\r\n')
r = random.Random(1234)
open('payload/sub/random.bin', 'wb').write(bytes(r.getrandbits(8) for _ in range(3000)))
open('payload/sub/repeat.txt', 'w').write('abcdefgh' * 4000)

def script(comp, solid, uni, out, uninst=False):
    lines = []
    if uni:
        lines.append(f'Unicode {uni}')
    lines += ['Name "fixture"', f'OutFile "{out}"', 'RequestExecutionLevel user', 'InstallDir "$TEMP\\nsisfixture"',
              'SetCompress off' if comp == 'stored' else f'SetCompressor /FINAL {"/SOLID " if solid else ""}{comp}',
              'Page instfiles', 'Section',
              'SetOutPath "$INSTDIR"', 'File "payload\\readme.txt"', 'SetOutPath "$INSTDIR\\sub"',
              'File "payload\\sub\\random.bin"', 'File "payload\\sub\\repeat.txt"', 'File "payload\\sub\\readme-copy.txt"',
              'SectionEnd']
    if uninst:
        # WriteUninstaller stores the uninstaller's icon patch and its own header in the data
        # section; the uninstaller is the exe stub with that patch applied, then that header.
        lines[-1:-1] = ['WriteUninstaller "$INSTDIR\\uninst.exe"']
        lines += ['Section "Uninstall"', 'Delete "$INSTDIR\\uninst.exe"', 'SectionEnd']
    return '\r\n'.join(lines) + '\r\n'

jobs = []
for comp in ('zlib', 'bzip2', 'lzma'):
    for solid in (False, True):
        tag = 'solid' if solid else 'nonsolid'
        jobs.append((NSIS3, comp, solid, 'true', f'nsis3-{comp}-{tag}.exe'))
        jobs.append((NSIS2, comp, solid, None, f'nsis2-{comp}-{tag}.exe'))
jobs.append((NSIS3, 'lzma', True, 'false', 'nsis3-lzma-solid-ansi.exe'))
jobs.append((NSIS3, 'zlib', False, 'false', 'nsis3-zlib-nonsolid-ansi.exe'))
jobs = [job + (False,) for job in jobs]
jobs.append((NSIS3, 'lzma', True, 'true', 'nsis3-lzma-solid-uninst.exe', True))
jobs.append((NSIS3, 'zlib', False, 'true', 'nsis3-zlib-nonsolid-uninst.exe', True))
jobs.append((NSIS2, 'bzip2', True, None, 'nsis2-bzip2-solid-uninst.exe', True))
jobs.append((NSIS2, 'lzma', False, None, 'nsis2-lzma-nonsolid-uninst.exe', True))
jobs.append((NSIS3, 'stored', False, 'true', 'nsis3-stored-uninst.exe', True))
for exe, comp, solid, uni, out, uninst in jobs:
    nsi = out[:-4] + '.nsi'
    open(nsi, 'w').write(script(comp, solid, uni, out, uninst))
    p = subprocess.run([exe, '-V1', nsi], capture_output=True, text=True)
    print(out, p.returncode, os.path.getsize(out) if os.path.exists(out) else -1, p.stdout[-300:], p.stderr[-300:])
