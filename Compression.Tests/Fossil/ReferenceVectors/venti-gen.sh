#!/bin/bash
# Builds a Venti arena partition and two Fossil volumes that lean on it, with plan9port:
#  venti.arenas  - fmtarenas'd arena partition (4 MiB arenas) the venti server filled
#  archived.img  - a Fossil volume whose tree was written, snapshotted and archived to that Venti (snap -a)
#  restored.img  - flfmt -v <score>: a fresh Fossil volume whose root is the archived score, every block on Venti
#  vac.score     - the score of a vac archive of the same payload (vac -h)
#  manifest      - what plan9port reads back: the restored volume served by fossil over 9P, and unvac of the vac archive
# Usage: venti-gen.sh <output-dir>   (expects plan9port built in ~/plan9port)
set -u
out="${1:?usage: venti-gen.sh <output-dir>}"; mkdir -p "$out"; out=$(readlink -f "$out")
export PLAN9=$HOME/plan9port PATH=$PATH:$HOME/plan9port/bin:$HOME/plan9port/bin/fossil:$HOME/plan9port/bin/venti
d=$(mktemp -d); vport=$((20000 + RANDOM % 20000)); fport=$((vport + 1)); V="tcp!127.0.0.1!$vport"; A="tcp!127.0.0.1!$fport"
echo "work $d"
truncate -s 24M $d/arenas; truncate -s 4M $d/isect
fmtarenas -a 4m arenas0 $d/arenas > $d/fmt.log 2>&1 || { cat $d/fmt.log; exit 1; }
fmtisect isect0 $d/isect >> $d/fmt.log 2>&1 || { cat $d/fmt.log; exit 1; }
printf 'index main\nisect %s\narenas %s\n' $d/isect $d/arenas > $d/venti.conf
fmtindex $d/venti.conf >> $d/fmt.log 2>&1 || { cat $d/fmt.log; exit 1; }
venti -c $d/venti.conf -a "$V" -h "tcp!127.0.0.1!$((vport + 2))" > $d/venti.log 2>&1 &
vpid=$!
for i in $(seq 1 40); do sleep 0.5; grep -q "venti started\|ready" $d/venti.log 2>/dev/null && break; done
sleep 1; echo "venti pid $vpid"; tail -3 $d/venti.log
export venti="$V"

# payload
p=$d/payload; mkdir -p $p/docs/deep $p/empty-dir
printf 'hello from venti\n' > $p/hello.txt
: > $p/empty
yes "venti archived pointer tree " | head -c 300000 > $p/big.txt
head -c 70000 /dev/urandom > $p/docs/random.bin
printf 'notes\n' > $p/docs/notes.txt
head -c 5000 /dev/urandom > $p/docs/deep/leaf.dat
touch -d @1200000000 $p $p/* $p/docs/* $p/docs/deep/*

# vac of the payload
vac -h "$V" -f $d/p.vac $p > $d/vac.log 2>&1; echo "vac rc=$?"; cat $d/p.vac; echo
sed -n 's/^vac:\([0-9a-f]*\)$/\1/p' $d/p.vac > $out/vac.score

# fossil volume written then archived
img=$d/archived.img; truncate -s 16M $img
flfmt -y -b 4096 $img > /dev/null
cat > $d/cons.txt <<EOF
fsys main config $img
fsys main venti $V
fsys main open -AWP
fsys main create /active/docs sys sys d775
fsys main create /active/docs/deep sys sys d755
fsys main create /active/empty-dir sys sys d755
fsys main create /active/hello.txt sys sys 664
fsys main create /active/empty sys sys 644
fsys main create /active/big.txt sys sys 644
fsys main create /active/docs/random.bin sys sys 644
fsys main create /active/docs/notes.txt sys sys 600
fsys main create /active/docs/deep/leaf.dat sys sys 644
listen $A
EOF
mkfifo $d/con
(script -qfc "fossil -t -c '. $d/cons.txt'" /dev/null < $d/con > $d/fossil.log 2>&1 &)
exec 3> $d/con
for i in $(seq 1 30); do 9p -a "$A" ls / >/dev/null 2>&1 && break; sleep 0.5; done
for f in hello.txt empty big.txt docs/random.bin docs/notes.txt docs/deep/leaf.dat; do 9p -a "$A" write /$f < $p/$f; done
trap '' PIPE
echo "fsys main snap -a" >&3
for i in $(seq 1 120); do sleep 1; grep -q "archive vac:" $d/fossil.log && break; done; grep -a "archive vac:" $d/fossil.log
echo "fsys main sync" >&3; sleep 2
{ echo "fsys main halt"; sleep 2; } >&3 2>/dev/null
exec 3>&- 2>/dev/null
pkill -f "$d/cons.txt" 2>/dev/null; sleep 2
echo "== fossil log"; tr -d '\r' < $d/fossil.log | grep -v '^$' | tail -20
score=$($PLAN9/bin/fossil/last $img 2>&1); echo "last: $score"
echo "$score" | sed 's/^vac://' > $out/archive.score

# restored volume: every block on venti
rimg=$d/restored.img; truncate -s 8M $rimg
flfmt -y -b 4096 -v "${score#vac:}" $rimg 2>&1 | tail -3

# read back the restored volume through fossil + 9P, and the vac archive through unvac
cat > $d/cons2.txt <<EOF
fsys main config $rimg
fsys main venti $V
fsys main open -AWP
listen $A
EOF
mkfifo $d/con2
(script -qfc "fossil -t -c '. $d/cons2.txt'" /dev/null < $d/con2 > $d/fossil2.log 2>&1 &)
exec 4> $d/con2
for i in $(seq 1 30); do 9p -a "$A" ls / >/dev/null 2>&1 && break; sleep 0.5; done
walk() {
  local pth="$1"
  9p -a "$A" ls -l "$pth" 2>/dev/null | while read -r mode a b owner group size m dd t name; do
    local q="${pth%/}/$name"
    if [ "${mode:0:1}" = "d" ]; then echo "D ${q#/} $mode"; walk "$q"
    else echo "F ${q#/} $size $(9p -a "$A" read "$q" | sha256sum | cut -d' ' -f1) $mode"; fi
  done
}
walk / | sort > $out/restored.manifest
{ echo "fsys main halt"; sleep 2; } >&4 2>/dev/null
exec 4>&- 2>/dev/null
pkill -f "$d/cons2.txt" 2>/dev/null; sleep 1
echo "== restored manifest"; cat $out/restored.manifest; tr -d '\r' < $d/fossil2.log | grep -v '^$' | tail -5
mkdir $d/un && (cd $d/un && unvac -h "$V" $d/p.vac > /dev/null 2>&1; echo "unvac rc=$?")
(cd $d/un && find . -type f | sort | while read -r f; do echo "$(sha256sum < "$f" | cut -d' ' -f1)  ${f#./}"; done) > $out/vac.manifest
echo "== vac manifest"; cat $out/vac.manifest
kill $vpid; sleep 2
cp $d/arenas $out/venti.arenas; cp $img $out/archived.img; cp $rimg $out/restored.img; cp $d/p.vac $out/payload.vac
ls -la $out
echo "$d" > $out/workdir
