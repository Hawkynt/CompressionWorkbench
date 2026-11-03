#!/bin/bash
# Regenerates the Reiser4 reference volumes: mkfs.reiser4 (reiser4progs 1.2.2) formats each one
# with a plugin profile, a Reiser4 kernel fills it through its own write path (populate.sh, run by
# ../KernelOracle/kernel-run.sh), and the kernel's read-back is kept as <name>.manifest.
# journal-replay.img is captured between a kernel commit and its write-back. Documentation, not a
# build input; run from this directory under Linux or WSL with qemu-system-x86_64.
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
RUN="$HERE/../KernelOracle/kernel-run.sh"
bash "$RUN" --prepare
W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT
mkdir -p "$W/payload"
for n in 0 1 100 4095 4096 4097; do head -c $n /dev/urandom > "$W/payload/f_$n"; done
for i in $(seq 1 1200); do echo "line $i: the quick brown fox jumps over the lazy dog $((i*7919 % 1000))"; done > "$W/payload/f_text"

volume() {
  local name=$1; shift
  truncate -s 0 "$W/$name.img"; truncate -s 16M "$W/$name.img"
  mkfs.reiser4 -fffy -U 11111111-2222-3333-4444-555555555555 -L ref "$@" "$W/$name.img" >/dev/null
  bash "$RUN" "$W/$name.img" "$HERE/populate.sh" "$W/payload" > "$W/$name.log"
  sed -n '/===MANIFEST===/,/===END-MANIFEST===/p' "$W/$name.log" | grep -v '===' > "$HERE/$name.manifest"
  gzip -9 -n -c "$W/$name.img" > "$HERE/$name.img.gz"
  echo "$name: $(wc -l < "$HERE/$name.manifest") objects; $(fsck.reiser4 --check -y "$W/$name.img" 2>&1 | grep -E 'consistent|corrupt' | head -1)"
}

if [ -z "$JOURNAL_ONLY" ]; then
volume default
volume gzip1-force-8k   -o compress=gzip1,compressMode=force,cluster=8K
volume zstd1-latt-16k   -o compress=zstd1,compressMode=latt,cluster=16K
volume lzo1-none-4k     -o compressMode=none,cluster=4K
volume reg40-smart      -o create=reg40
volume reg40-tails      -o create=reg40,formatting=tails
volume reg40-node41     -o create=reg40,formatting=extents,node=node41
volume node41           -o node=node41
volume tea-lexic        -o create=reg40,hash=tea_hash,fibration=lexic_fibre
volume fnv1-dot-o       -o create=reg40,hash=fnv1_hash,fibration=dot_o_fibre
volume deg-ext-3        -o create=reg40,hash=deg_hash,fibration=ext_3_fibre
volume rupasov          -o create=reg40,hash=rupasov_hash
fi

# A transaction the kernel committed but had not yet written back: writes are throttled so the
# write-back takes seconds, and the image is copied the moment the journal header moves past the
# footer.
cat > "$W/journal.sh" <<'EOF'
mount -t reiser4 /dev/vda /mnt || exit 1
i=0; while [ $i -lt 10 ]; do head -c 40000 /payload/f_text > /mnt/big$i; echo "small $i" > /mnt/s$i; i=$((i+1)); done
mkdir /mnt/dir
sync
i=0; while [ $i -lt 10 ]; do dd if=/payload/f_text of=/mnt/big$i bs=4096 skip=$((i+1)) seek=2 count=5 conv=notrunc 2>/dev/null; echo "changed $i" > /mnt/s$i; mv /mnt/s$i /mnt/dir/; i=$((i+1)); done
sync
umount /mnt
EOF
sed 's/cache=unsafe/cache=unsafe,throttling.bps-write=100000/' "$RUN" > "$W/slow.sh"
# A capture counts only when it is a later commit (the footer has moved once), fsck.reiser4 replays
# it to a consistent volume, and the kernel mounts it.
for attempt in $(seq 1 12); do
  truncate -s 0 "$W/j.img"; truncate -s 16M "$W/j.img"
  mkfs.reiser4 -fffy -U 11111111-2222-3333-4444-555555555555 -L journal -o create=reg40 "$W/j.img" >/dev/null
  rm -f "$W/journal-replay.img"
  ( bash "$W/slow.sh" "$W/j.img" "$W/journal.sh" "$W/payload" > /dev/null ) &
  P=$!
  while kill -0 $P 2>/dev/null; do
    c=$(od -An -t u8 -j $((19*4096)) -N 8 "$W/j.img" | tr -d ' ')
    f=$(od -An -t u8 -j $((20*4096)) -N 8 "$W/j.img" | tr -d ' ')
    if [ "$c" != "$f" ] && [ "$f" != "0" ]; then cp "$W/j.img" "$W/journal-replay.img"; break; fi
    sleep 0.02
  done
  wait $P
  [ -f "$W/journal-replay.img" ] || continue
  cp "$W/journal-replay.img" "$W/f.img"
  fsck.reiser4 --check -y "$W/f.img" > "$W/f.log" 2>&1 || true
  grep -q "Replaying transaction" "$W/f.log" && grep -q "FS is consistent" "$W/f.log" || continue
  cp "$W/journal-replay.img" "$W/k.img"
  bash "$RUN" "$W/k.img" "$HERE/../KernelOracle/manifest.sh" > "$W/k.log"
  sed -n '/===MANIFEST===/,/===END-MANIFEST===/p' "$W/k.log" | grep -v '===' > "$W/journal-replay.manifest"
  grep -q '^./big9|' "$W/journal-replay.manifest" && break
done
cp "$W/journal-replay.manifest" "$HERE/journal-replay.manifest"
gzip -9 -n -c "$W/journal-replay.img" > "$HERE/journal-replay.img.gz"
echo "journal-replay: $(debugfs.reiser4 -j "$W/journal-replay.img" </dev/null 2>/dev/null | grep last | tr '\n' ' ')"
