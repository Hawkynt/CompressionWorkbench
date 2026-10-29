import os, random, subprocess, sys
# usage: mkiexpress.py <workdir> <name> [compression]  -- builds an IExpress package with C:\Windows\System32\iexpress.exe
W, name = sys.argv[1], sys.argv[2]
comp = sys.argv[3] if len(sys.argv) > 3 else None
W = os.path.abspath(W)
os.makedirs(os.path.join(W, 'payload'), exist_ok=True)
p = os.path.join(W, 'payload')
open(os.path.join(p, 'readme.txt'), 'wb').write(b'Hello from an IExpress fixture.\r\nLine two.\r\n')
r = random.Random(1234)
open(os.path.join(p, 'random.bin'), 'wb').write(bytes(r.getrandbits(8) for _ in range(3000)))
open(os.path.join(p, 'repeat.txt'), 'w').write('abcdefgh' * 4000)
files = ['readme.txt', 'random.bin', 'repeat.txt']
target = os.path.join(W, name + '.exe')
sed = ['[Version]', 'Class=IEXPRESS', 'SEDVersion=3', '[Options]', 'PackagePurpose=ExtractOnly', 'ShowInstallProgramWindow=0',
       'HideExtractAnimation=1', 'UseLongFileName=1', 'InsideCompressed=0', 'CAB_FixedSize=0', 'CAB_ResvCodeSigning=0',
       'RebootMode=N', 'InstallPrompt=%InstallPrompt%', 'DisplayLicense=%DisplayLicense%', 'FinishMessage=%FinishMessage%',
       'TargetName=%TargetName%', 'FriendlyName=%FriendlyName%', 'AppLaunched=%AppLaunched%', 'PostInstallCmd=%PostInstallCmd%',
       'AdminQuietInstCmd=%AdminQuietInstCmd%', 'UserQuietInstCmd=%UserQuietInstCmd%', 'SourceFiles=SourceFiles']
if comp:
    sed.append('CompressionType=' + comp)
sed += ['[Strings]', 'InstallPrompt=', 'DisplayLicense=', 'FinishMessage=', f'TargetName={target}', 'FriendlyName=Fixture',
        'AppLaunched=', 'PostInstallCmd=<None>', 'AdminQuietInstCmd=', 'UserQuietInstCmd=']
for i, f in enumerate(files):
    sed.append(f'FILE{i}="{f}"')
sed += ['[SourceFiles]', f'SourceFiles0={p}\\', '[SourceFiles0]']
sed += [f'%FILE{i}%=' for i in range(len(files))]
sedpath = os.path.join(W, name + '.sed')
open(sedpath, 'w').write('\r\n'.join(sed) + '\r\n')
res = subprocess.run([r'C:\Windows\System32\iexpress.exe', '/N', '/Q', sedpath], capture_output=True, text=True, timeout=300)
print(name, res.returncode, os.path.getsize(target) if os.path.exists(target) else -1)
