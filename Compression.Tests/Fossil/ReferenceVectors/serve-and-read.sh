#!/bin/bash
# Serves a Fossil volume with plan9port's fossil server and reads its active tree back over 9P.
# Prints "D path mode" for every directory and "F path size sha256 mode" for every file, sorted,
# then the server's console log after "== log".
# With a second argument the server then creates and writes /active/added.txt, syncs, and the
# changed volume is copied to that path.
# Usage: serve-and-read.sh <volume> [changed-volume]   (expects plan9port built in ~/plan9port)
. "$(dirname "$(readlink -f "$0")")/../../Support/cwb-rmtree.sh"
set -u
img="${1:?usage: serve-and-read.sh <volume> [changed-volume]}"
export PLAN9=$HOME/plan9port PATH=$PATH:$HOME/plan9port/bin:$HOME/plan9port/bin/fossil
d=$(mktemp -d); port=$((20000 + RANDOM % 20000)); A="tcp!127.0.0.1!$port"
cp "$img" $d/v.img
cat > $d/cons.txt <<EOF
fsys main config $d/v.img
fsys main open -AWVP
listen $A
EOF
mkfifo $d/con
(script -qfc "fossil -t -c '. $d/cons.txt'" /dev/null < $d/con > $d/fossil.log 2>&1 &)
exec 3> $d/con
for i in $(seq 1 40); do 9p -a "$A" ls / >/dev/null 2>&1 && break; sleep 0.5; done
walk() {
  local p="$1"
  9p -a "$A" ls -l "$p" 2>/dev/null | while read -r mode a b owner group size m dd t name; do
    local q="${p%/}/$name"
    if [ "${mode:0:1}" = "d" ]; then
      echo "D ${q#/} $mode"
      walk "$q"
    else
      local sum
      sum=$(9p -a "$A" read "$q" | sha256sum | cut -d' ' -f1)
      echo "F ${q#/} $size $sum $mode"
    fi
  done
}
walk / | sort
trap '' PIPE
if [ -n "${2:-}" ]; then
  # write through the server too: it must find free blocks, labels and qids it can extend
  echo "fsys main create /active/added.txt adm adm 664" >&3; sleep 1
  printf 'written by the fossil server\n' | 9p -a "$A" write /added.txt
  echo "== added $(9p -a "$A" read /added.txt | sha256sum | cut -d' ' -f1)"
  { echo "fsys main sync"; sleep 2; } >&3 2>/dev/null
fi
{ echo "fsys main halt"; sleep 1; } >&3 2>/dev/null
exec 3>&- 2>/dev/null
pkill -f "$d/cons.txt" 2>/dev/null; sleep 1
[ -n "${2:-}" ] && cp $d/v.img "$2"
echo "== log"; tr -d '\r' < $d/fossil.log | grep -v '^$' | head -20
cwb_rmtree "$d"
