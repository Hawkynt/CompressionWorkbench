import os, random, subprocess, sys
# usage: mkburn.py <workdir> <wix bin dir> <name>  -- builds a WiX v3 Burn bundle with candle/light (never runs it)
W, wix, name = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2]), sys.argv[3]
p = os.path.join(W, 'payload')
os.makedirs(p, exist_ok=True)
open(os.path.join(p, 'readme.txt'), 'wb').write(b'Hello from a Burn bundle fixture.\r\nLine two.\r\n')
r = random.Random(1234)
open(os.path.join(p, 'random.bin'), 'wb').write(bytes(r.getrandbits(8) for _ in range(3000)))
open(os.path.join(p, 'repeat.txt'), 'w').write('abcdefgh' * 4000)
# The chained package is never executed; any bytes do for the compiler.
open(os.path.join(p, 'package.exe'), 'wb').write(b'MZ' + b'\0' * 510)
wxs = f'''<?xml version="1.0" encoding="UTF-8"?>
<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi" xmlns:bal="http://schemas.microsoft.com/wix/BalExtension">
  <Bundle Name="Fixture" Version="1.0.0.0" Manufacturer="Fixture" UpgradeCode="6f2b1c55-3c0e-4a39-9c7c-2b9a6d8e1f01">
    <BootstrapperApplicationRef Id="WixStandardBootstrapperApplication.HyperlinkLicense">
      <bal:WixStandardBootstrapperApplication LicenseUrl="" SuppressOptionsUI="yes" />
    </BootstrapperApplicationRef>
    <Chain>
      <ExePackage Id="Pkg" SourceFile="payload\\package.exe" Compressed="yes" DetectCondition="1" PerMachine="no" Vital="no">
        <Payload SourceFile="payload\\readme.txt" Name="docs\\readme.txt" Compressed="yes" />
        <Payload SourceFile="payload\\random.bin" Name="data\\random.bin" Compressed="yes" />
        <Payload SourceFile="payload\\repeat.txt" Name="data\\repeat.txt" Compressed="yes" />
      </ExePackage>
    </Chain>
  </Bundle>
</Wix>
'''
open(os.path.join(W, name + '.wxs'), 'w').write(wxs)
os.chdir(W)
r1 = subprocess.run([os.path.join(wix, 'candle.exe'), '-nologo', '-ext', 'WixBalExtension', name + '.wxs'], capture_output=True, text=True)
r2 = subprocess.run([os.path.join(wix, 'light.exe'), '-nologo', '-ext', 'WixBalExtension', '-out', name + '.exe', name + '.wixobj'], capture_output=True, text=True)
print(r1.returncode, r1.stdout[-500:], r2.returncode, r2.stdout[-800:])
print(name, os.path.getsize(name + '.exe') if os.path.exists(name + '.exe') else -1)
