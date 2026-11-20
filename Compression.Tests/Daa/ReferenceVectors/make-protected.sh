#!/bin/bash
# Builds two ISOs of generated files and converts them with PowerISO 1.3: a password-protected DAA
# (small ISO) and a split, password-protected DAA (larger ISO, -volsize 300K, its minimum).
# usage: make-protected.sh <poweriso binary> <work directory> <this directory>
P=${1:?poweriso binary}
W=${2:?work directory}
V=${3:?reference vector directory}
rm -rf $W && mkdir -p $W/a/sub $W/b && cd $W || exit 1
python3 - <<'EOF'
import random
r = random.Random(30)
words = "power iso daa image volume password chunk table deflate split".split()
open('a/notes.txt', 'wb').write(" ".join(r.choice(words) for _ in range(20000)).encode())
open('a/random.bin', 'wb').write(bytes(r.getrandbits(8) for _ in range(5000)))
open('a/sub/small.txt', 'wb').write(b'small file\n')
open('b/random.bin', 'wb').write(bytes(r.getrandbits(8) for _ in range(340000)))
open('b/notes.txt', 'wb').write(" ".join(r.choice(words) for _ in range(30000)).encode())
EOF
find a b -exec touch -d '2024-05-06 07:08:10 UTC' {} +
genisoimage -quiet -r -J -V CWBDAAPW -o a.iso a
genisoimage -quiet -r -J -V CWBDAASPLIT -o b.iso b
sha256sum a.iso b.iso; ls -la a.iso b.iso
rm -f "$V"/pw*.daa "$V"/split*.daa "$V"/pw-src.iso.gz
$P convert a.iso -o pw.daa -setpassword Secret-30 > /dev/null 2>&1
$P convert b.iso -o splitpw.daa -volsize 300K -setpassword Secret-30 > /dev/null 2>&1
ls -la *.daa
echo Secret-30 | $P convert pw.daa -o a-back.iso -ot iso > /dev/null 2>&1
cmp a-back.iso a.iso && echo "PowerISO reads pw.daa back to a.iso"
echo Secret-30 | $P convert splitpw.part01.daa -o b-back.iso -ot iso > /dev/null 2>&1
cmp b-back.iso b.iso && echo "PowerISO reads splitpw back to b.iso"
cp pw.daa splitpw.part0*.daa "$V/"
ls -la "$V"
