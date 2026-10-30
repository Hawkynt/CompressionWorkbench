import os, random, subprocess, sys
# usage: mkevb.py <workdir> <enigmavbconsole.exe> <host exe>  -- writes payload + projects, boxes them
work, console, host = sys.argv[1:4]
os.makedirs(work, exist_ok=True)
pay = os.path.join(work, 'payload')
os.makedirs(os.path.join(pay, 'data', 'sub'), exist_ok=True)
rnd = random.Random(20261030)
files = {
    'hello.txt': b'Hello from an Enigma Virtual Box.\r\n' * 3,
    'empty.txt': b'',
    'data/random.bin': bytes(rnd.randrange(256) for _ in range(70000)),
    'data/sub/pattern.bin': b''.join(bytes([i % 251]) * 7 for i in range(30000)),
}
for rel, data in files.items():
    open(os.path.join(pay, *rel.split('/')), 'wb').write(data)


def file_xml(name, path, ind):
    p = '  ' * ind
    return (f'{p}<File>\n{p}  <Type>2</Type>\n{p}  <Name>{name}</Name>\n{p}  <File>{path}</File>\n'
            f'{p}  <ActiveX>False</ActiveX>\n{p}  <ActiveXInstall>False</ActiveXInstall>\n{p}  <Action>0</Action>\n'
            f'{p}  <OverwriteDateTime>False</OverwriteDateTime>\n{p}  <OverwriteAttributes>False</OverwriteAttributes>\n'
            f'{p}  <PassCommandLine>False</PassCommandLine>\n{p}  <HideFromDialogs>0</HideFromDialogs>\n{p}</File>\n')


def folder_xml(name, children, ind):
    p = '  ' * ind
    return (f'{p}<File>\n{p}  <Type>3</Type>\n{p}  <Name>{name}</Name>\n{p}  <Action>0</Action>\n'
            f'{p}  <OverwriteDateTime>False</OverwriteDateTime>\n{p}  <OverwriteAttributes>False</OverwriteAttributes>\n'
            f'{p}  <HideFromDialogs>0</HideFromDialogs>\n{p}  <Files>\n{children}{p}  </Files>\n{p}</File>\n')


def tree():
    f = lambda rel: os.path.join(pay, *rel.split('/'))
    sub = folder_xml('sub', file_xml('pattern.bin', f('data/sub/pattern.bin'), 7), 6)
    data = folder_xml('data', file_xml('random.bin', f('data/random.bin'), 6) + sub, 5)
    top = file_xml('hello.txt', f('hello.txt'), 5) + file_xml('empty.txt', f('empty.txt'), 5) + data
    return folder_xml('%DEFAULT FOLDER%', top, 3)


for name, compress in [('evb-stored', False), ('evb-compressed', True)]:
    out = os.path.join(work, name + '.exe')
    proj = os.path.join(work, name + '.evb')
    xml = ('<?xml version="1.0" encoding="windows-1252"?>\n<>\n'
           f'  <InputFile>{host}</InputFile>\n  <OutputFile>{out}</OutputFile>\n'
           f'  <Files>\n    <Enabled>True</Enabled>\n    <DeleteExtractedOnExit>False</DeleteExtractedOnExit>\n'
           f'    <CompressFiles>{compress}</CompressFiles>\n    <Files>\n{tree()}    </Files>\n  </Files>\n'
           '  <Registries>\n    <Enabled>False</Enabled>\n  </Registries>\n'
           '  <Packaging>\n    <Enabled>False</Enabled>\n  </Packaging>\n'
           '  <Options>\n    <ShareVirtualSystem>False</ShareVirtualSystem>\n'
           '    <MapExecutableWithTemporaryFile>True</MapExecutableWithTemporaryFile>\n    <TemporaryFileMask/>\n'
           '    <AllowRunningOfVirtualExeFiles>True</AllowRunningOfVirtualExeFiles>\n'
           '    <ProcessesOfAnyPlatforms>False</ProcessesOfAnyPlatforms>\n  </Options>\n'
           '  <Storage>\n    <Files>\n      <Enabled>False</Enabled>\n      <Folder>%DEFAULT FOLDER%\\</Folder>\n'
           '      <RandomFileNames>False</RandomFileNames>\n      <EncryptContent>False</EncryptContent>\n    </Files>\n  </Storage>\n</>\n')
    open(proj, 'w', encoding='cp1252', newline='\r\n').write(xml)
    p = subprocess.run([console, proj], capture_output=True, text=True, timeout=300)
    print(name, p.returncode, p.stdout[-600:], p.stderr[-300:])
    if os.path.exists(out):
        print(' ->', out, os.path.getsize(out))
