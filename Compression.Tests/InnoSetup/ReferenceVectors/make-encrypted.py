import os, random, subprocess, sys
# usage: make-encrypted.py <workdir> <tag> <ISCC.exe> <password> [variants...]
#   variant = compression[:solid][:full]  ("full" = Encryption=full, Inno Setup 6.5+)
# Same payload as make-fixtures.py; every file encrypted with <password>. Compilers before 6.4
# need ISCrypt.dll beside ISCC.exe. The script is written as UTF-8 with a BOM, for ANSI
# compilers (tag ending in 'a') in Windows-1252.
W, tag, iscc, password = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
variants = sys.argv[5:] or ['zip']
os.makedirs(os.path.join(W, 'payload', 'sub'), exist_ok=True)
os.chdir(W)
open('payload/readme.txt', 'wb').write(b'Hello from an Inno Setup fixture.\r\nLine two.\r\n')
open('payload/sub/readme-copy.txt', 'wb').write(b'Hello from an Inno Setup fixture.\r\nLine two.\r\n')
r = random.Random(1234)
open('payload/sub/random.bin', 'wb').write(bytes(r.getrandbits(8) for _ in range(3000)))
open('payload/sub/repeat.txt', 'w').write('abcdefgh' * 4000)
for v in variants:
    parts = v.split(':')
    comp, solid, full = parts[0], 'solid' in parts, 'full' in parts
    name = f'inno{tag}-enc{"full" if full else ""}-{comp}{"-solid" if solid else ""}'
    iss = f'''[Setup]
AppName=Fixture
AppVersion=1.0
AppVerName=Fixture 1.0
DefaultDirName={{tmp}}\\innofixture
OutputDir=.
OutputBaseFilename={name}
Compression={comp}
SolidCompression={"yes" if solid else "no"}
PrivilegesRequired=lowest
Uninstallable=no
DisableProgramGroupPage=yes
Password={password}
Encryption={"full" if full else "yes"}

[Files]
Source: "payload\\readme.txt"; DestDir: "{{app}}"
Source: "payload\\sub\\random.bin"; DestDir: "{{app}}\\sub"
Source: "payload\\sub\\repeat.txt"; DestDir: "{{app}}\\sub"
Source: "payload\\sub\\readme-copy.txt"; DestDir: "{{app}}\\sub"
'''
    open(name + '.iss', 'w', encoding='cp1252' if tag.endswith('a') else 'utf-8-sig').write(iss)
    p = subprocess.run([iscc, '/Q', name + '.iss'], capture_output=True, text=True)
    out = name + '.exe'
    print(name, p.returncode, os.path.getsize(out) if os.path.exists(out) else -1, p.stdout[-300:], p.stderr[-300:])
