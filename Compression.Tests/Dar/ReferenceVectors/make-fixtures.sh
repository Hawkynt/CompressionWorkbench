#!/bin/bash
# Builds the dar 2.7.13 reference archives (README.md) with the system dar, by default into the
# directory holding this script. The encrypted archive's size varies from run to run (dar pads it
# with random-length elastic buffers); the checked-in one is the smallest of several runs.
set -e
OUT=${1:-$(dirname "$(readlink -f "$0")")}
S=/tmp/cwbdar/src
rm -rf /tmp/cwbdar; mkdir -p $S/dir/deeper "$OUT" /tmp/cwbdar/empty
rm -f "$OUT"/tree*.1.dar "$OUT"/multi.*.dar "$OUT"/empty.1.dar "$OUT"/encrypted.1.dar
cd $S
printf 'hello dar\n' > hello.txt
: > empty.bin
python3 - <<'EOF'
q = b'The quick brown fox jumps over the lazy dog. ' * 50
open('text.txt', 'wb').write(q)
open('escape.bin', 'wb').write(b'A' * 10 + bytes([0xAD, 0xFD, 0xEA, 0x77, 0x21, 0x46]) + b'B' * 10
                               + bytes([0xAE, 0xFD, 0xEA, 0x77, 0x21, 0x58]) * 3 + b'C' * 200)
open('sparse.bin', 'wb').write(b'head' + bytes(70000) + b'tail')
x, out = 2463534242, bytearray()
for _ in range(600):
    x ^= (x << 13) & 0xFFFFFFFF; x ^= x >> 17; x ^= (x << 5) & 0xFFFFFFFF
    out.append(x & 0xFF)
open('noise.bin', 'wb').write(out)
open('n' * 200, 'wb').write(b'long')
EOF
printf 'deep\n' > dir/deeper/file.txt
ln -s hello.txt link
ln hello.txt hard.txt
mkfifo fifo
chmod 600 noise.bin; chmod 751 dir
TZ=UTC touch -d '2001-02-03 04:05:06.123456789' hello.txt
cd /tmp/cwbdar
dar -Q -c "$OUT/tree" -R $S > /dev/null
dar -Q -c "$OUT/tree-notape" -at -R $S > /dev/null
for z in gzip bzip2 xz zstd lz4 lzo; do dar -Q -c "$OUT/tree-$z" -z$z -R $S > /dev/null; done
dar -Q -c "$OUT/tree-zstd-block" -zzstd:6:16k -R $S > /dev/null
dar -Q -c "$OUT/tree-gzip-block" -zgzip:6:16k -R $S > /dev/null
dar -Q -c "$OUT/multi" -S 3k -s 2k -R $S > /dev/null
dar -Q -c "$OUT/empty" -R /tmp/cwbdar/empty > /dev/null
dar -Q -c "$OUT/encrypted" -K aes:cwb-fixture -R /tmp/cwbdar/empty > /dev/null
dar -V | head -2
ls -la "$OUT"
