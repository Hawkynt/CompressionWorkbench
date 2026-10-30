import os, random, subprocess, sys
# usage: mkinno.py <workdir> <tag> <ISCC.exe> [variants...]   variant = compression[:solid]
W, tag, iscc = sys.argv[1], sys.argv[2], sys.argv[3]
variants = sys.argv[4:] or ['zip', 'bzip', 'lzma', 'lzma2:solid', 'none']
os.makedirs(os.path.join(W, 'payload', 'sub'), exist_ok=True)
os.chdir(W)
open('payload/readme.txt', 'wb').write(b'Hello from an Inno Setup fixture.\r\nLine two.\r\n')
open('payload/sub/readme-copy.txt', 'wb').write(b'Hello from an Inno Setup fixture.\r\nLine two.\r\n')
r = random.Random(1234)
open('payload/sub/random.bin', 'wb').write(bytes(r.getrandbits(8) for _ in range(3000)))
open('payload/sub/repeat.txt', 'w').write('abcdefgh' * 4000)
for v in variants:
    comp, _, solid = v.partition(':')
    name = f'inno{tag}-{comp}{"-solid" if solid else ""}'
    iss = f'''[Setup]
AppName=Fixture
AppVersion=1.0
DefaultDirName={{tmp}}\\innofixture
OutputDir=.
OutputBaseFilename={name}
Compression={comp}
SolidCompression={"yes" if solid else "no"}
PrivilegesRequired=lowest
Uninstallable=no
DisableProgramGroupPage=yes

[Files]
Source: "payload\\readme.txt"; DestDir: "{{app}}"
Source: "payload\\sub\\random.bin"; DestDir: "{{app}}\\sub"
Source: "payload\\sub\\repeat.txt"; DestDir: "{{app}}\\sub"
Source: "payload\\sub\\readme-copy.txt"; DestDir: "{{app}}\\sub"
'''
    open(name + '.iss', 'w').write(iss)
    p = subprocess.run([iscc, '/Q', name + '.iss'], capture_output=True, text=True)
    out = name + '.exe'
    print(name, p.returncode, os.path.getsize(out) if os.path.exists(out) else -1, p.stdout[-300:], p.stderr[-300:])
