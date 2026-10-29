#!/bin/bash
# Builds fossil.img.gz + fossil.manifest with plan9port (commit 1f13969): flfmt formats a
# 16 MiB volume with 4 KiB blocks, the fossil server (console on a pty via script(1))
# creates the tree, files are written over 9P with plan9port's 9p client, a local snapshot
# is taken, and hello.txt is rewritten after it. The manifest is what that server reads
# back over 9P: sha256 and path from the Fossil root.
# Usage: generate.sh <output-dir>   (expects plan9port built in ~/plan9port)
set -u
out="${1:?usage: generate.sh <output-dir>}"; mkdir -p "$out"
export PLAN9=$HOME/plan9port PATH=$PATH:$HOME/plan9port/bin:$HOME/plan9port/bin/fossil
d=$(mktemp -d); img=$d/fossil.img; port=$((20000 + RANDOM % 20000)); A="tcp!127.0.0.1!$port"
truncate -s 16M $img
flfmt -y -b 4096 $img > /dev/null
cat > $d/cons.txt <<EOF
fsys main config $img
fsys main open -AWVP
fsys main create /active/docs sys sys d775
fsys main create /active/docs/deep sys sys d755
fsys main create /active/empty-dir sys sys d755
fsys main create /active/hello.txt sys sys 664
fsys main create /active/empty sys sys 644
fsys main create /active/big.bin sys sys 644
fsys main create /active/docs/notes.txt sys sys 600
fsys main create /active/docs/deep/leaf.dat sys sys 644
listen $A
EOF
mkfifo $d/con
(script -qfc "fossil -t -c '. $d/cons.txt'" /dev/null < $d/con > $d/fossil.log 2>&1 &)
exec 3> $d/con
for i in $(seq 1 30); do 9p -a "$A" ls / >/dev/null 2>&1 && break; sleep 0.5; done
printf 'hello from fossil\n' | 9p -a "$A" write /hello.txt
yes "fossil pointer tree " | head -c 900000 | 9p -a "$A" write /big.bin   # 900,000 bytes: 220 blocks > 204 pointers per 4 KiB block, so depth 2
printf 'private notes\n' | 9p -a "$A" write /docs/notes.txt
head -c 5000 /dev/urandom | 9p -a "$A" write /docs/deep/leaf.dat
trap '' PIPE
echo "fsys main snap" >&3; sleep 3
printf 'hello again, after the snapshot was taken\n' | 9p -a "$A" write /hello.txt
mkdir -p $d/x/active/docs/deep
for f in hello.txt empty big.bin docs/notes.txt docs/deep/leaf.dat; do 9p -a "$A" read /$f > $d/x/active/$f; done
{ echo "fsys main sync"; sleep 2; echo "fsys main halt"; } >&3 2>/dev/null; sleep 3
exec 3>&- 2>/dev/null
pkill -f "$d/cons.txt" 2>/dev/null; sleep 2
(cd $d/x && find . -type f | sort | sed 's#^\./##' | while read -r f; do echo "$(sha256sum < "$f" | cut -d' ' -f1)  $f"; done) > "$out/fossil.manifest"
gzip -9 -n -c $img > "$out/fossil.img.gz"
ls -la "$out"; cat "$out/fossil.manifest"
