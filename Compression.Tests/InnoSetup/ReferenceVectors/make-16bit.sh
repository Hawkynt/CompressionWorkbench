#!/bin/bash
# Builds inno1216-16bit.exe: a 16-bit Inno Setup 1.2.16 installer of generated inputs, then lists
# what innoextract extracts from it (path|size|sha256).
# usage: make-16bit.sh <dir holding COMPIL16.EXE, SETUP.E16, SETUPLDR.E16, UNINST.E16> <workdir> [innoextract]
# Needs wine (16-bit support) and xvfb-run. The compiler directory is innoextract's "app" folder
# of isetup16-1.2.16.exe (see README.md).
APP=${1:?compiler directory}
W=${2:?work directory}
IE=${3:-innoextract}
rm -rf $W && mkdir -p $W/in/sub && cd $W || exit 1
export WINEDEBUG=-all
python3 - <<'EOF'
import random
r = random.Random(12)
words = "the quick brown fox jumps over a lazy dog while installers copy files".split()
text = " ".join(r.choice(words) for _ in range(4000)).encode()
open('in/NOTES.TXT', 'wb').write(text)
open('in/DATA.BIN', 'wb').write(bytes(r.getrandbits(8) for _ in range(3000)))
open('in/EMPTY.TXT', 'wb').write(b'')
open('in/sub/INNER.TXT', 'wb').write(b'nested payload\r\n')
EOF
touch -d '1999-12-31 23:59:58' in/NOTES.TXT in/DATA.BIN in/EMPTY.TXT in/sub/INNER.TXT
printf '%s\r\n' \
  '[Setup]' 'Bits=16' 'AppName=CWB Probe' 'AppVerName=CWB Probe 1.2' 'AppCopyright=none' \
  'DefaultDirName={sd}\CWBPROBE' 'DefaultGroupName=CWB Probe' '' \
  '[Dirs]' 'Name: "{app}\LOGS"' '' \
  '[Files]' \
  'Source: "IN\NOTES.TXT"; DestDir: "{app}"' \
  'Source: "IN\DATA.BIN"; DestDir: "{app}\DATA"' \
  'Source: "IN\EMPTY.TXT"; DestDir: "{app}"' \
  'Source: "IN\SUB\INNER.TXT"; DestDir: "{app}\SUB"; Flags: isreadme' '' \
  '[Icons]' 'Name: "{group}\Notes"; Filename: "{app}\NOTES.TXT"' '' \
  '[INI]' 'Filename: "{app}\PROBE.INI"; Section: "Main"; Key: "Path"; String: "{app}"' '' \
  '[Registry]' 'Root: HKCR; Subkey: "CWBProbe"; ValueType: string; ValueData: "probe"' '' \
  '[Run]' 'Filename: "{app}\NOTES.TXT"' '' \
  '[UninstallDelete]' 'Type: files; Name: "{app}\PROBE.INI"' > PROBE.ISS
rm -rf OUTPUT
timeout 60 xvfb-run -a wine $APP/COMPIL16.EXE /cc PROBE.ISS > compile.log 2>&1
echo "compile exit $?"
ls -la OUTPUT

$IE -i OUTPUT/setup.exe 2>&1 | head -3
$IE -l OUTPUT/setup.exe 2>&1
rm -rf ex && mkdir ex && $IE -q -d ex OUTPUT/setup.exe
(cd ex && find . -type f | sort | while read -r f; do printf '%s|%s|%s\n' "${f#./}" "$(stat -c %s "$f")" "$(sha256sum "$f" | cut -d' ' -f1)"; done)
sha256sum OUTPUT/setup.exe in/*.* in/sub/*
