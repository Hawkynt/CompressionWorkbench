set -u
OUT="$1"
rm -rf "$OUT"; mkdir -p "$OUT/arj" "$OUT/xar" "$OUT/in"
cd "$OUT/in"
# Inputs: numbered text lines (long-distance repeats every 50 lines => matches far beyond 2 KiB),
# and a pseudo-random binary block from a fixed seed (no external entropy).
i=1; : > TEXT.TXT
while [ $i -le 300 ]; do printf 'line %05d: the quick brown fox jumps over the lazy dog #%02d\n' $i $((i % 50)) >> TEXT.TXT; i=$((i+1)); done
LC_ALL=C awk 'BEGIN{s=12345; for(i=0;i<3000;i++){s=(s*1103515245+12345)%2147483648; printf "%c", int(s/65536)%256}}' > RANDOM.BIN
cat RANDOM.BIN RANDOM.BIN > REPEAT.BIN
printf "a" > ONE.TXT
ls -l
sha256sum TEXT.TXT RANDOM.BIN REPEAT.BIN ONE.TXT
for m in 1 2 3 4; do
  arj a -m$m -e -y "$OUT/arj/m$m.arj" TEXT.TXT RANDOM.BIN REPEAT.BIN ONE.TXT > "$OUT/arj/m$m.log" 2>&1; echo "arj m$m exit $?"
  arj l "$OUT/arj/m$m.arj" 2>&1 | sed -n '1,30p'
done
arj 2>&1 | head -2
for c in none gzip bzip2 lzma xz; do
  bsdtar --format xar --options xar:compression=$c -cf "$OUT/xar/$c.xar" TEXT.TXT RANDOM.BIN REPEAT.BIN ONE.TXT; echo "bsdtar xar $c exit $?"
done
bsdtar --version
ls -l "$OUT/arj" "$OUT/xar"
for c in none gzip bzip2 lzma xz; do echo "== $c"; bsdtar -tvf "$OUT/xar/$c.xar"; done
