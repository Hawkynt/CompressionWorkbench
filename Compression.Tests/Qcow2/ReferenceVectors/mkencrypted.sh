#!/bin/sh
# Builds the encrypted QCOW2 vectors with qemu-img (8.2, Ubuntu 24.04 in WSL).
# Usage: sh mkencrypted.sh <workdir> <outdir>   (documentation, not a build input)
# The password of every image is Secr3t. qemu-img itself converts each image back to raw; the
# hashes of those guest disks go to qemu-encrypted.sha256; the images are stored gzipped.
set -e
work=$1; out=$2
rm -rf "$work"; mkdir -p "$work"; cd "$work"
python3 -c "
import random
r = random.Random(11)
g = bytearray(64 * 1024)
t = b'encrypted qcow2 sector '
for i in range(32768): g[i] = t[i % len(t)]
g[49152:] = bytes(r.getrandbits(8) for _ in range(16384))
open('guest.raw', 'wb').write(g)
"
S="--object secret,id=s0,data=Secr3t"
qemu-img convert $S -f raw -O qcow2 -o encrypt.format=aes,encrypt.key-secret=s0,cluster_size=4096 guest.raw enc-aes.qcow2
qemu-img convert $S -f raw -O qcow2 -o encrypt.format=luks,encrypt.key-secret=s0,encrypt.iter-time=10,encrypt.cipher-alg=aes-128,encrypt.cipher-mode=cbc,encrypt.ivgen-alg=essiv,encrypt.ivgen-hash-alg=sha256,encrypt.hash-alg=sha1,cluster_size=4096 guest.raw enc-luks-cbc-essiv.qcow2
for f in enc-aes enc-luks-cbc-essiv; do
  qemu-img convert $S --image-opts -O raw "driver=qcow2,file.filename=$f.qcow2,encrypt.key-secret=s0" $f.out
  printf '%s  %s.qcow2\n' "$(sha256sum < $f.out | cut -c1-64)" $f
done > qemu-encrypted.sha256
gzip -9 -n enc-aes.qcow2 enc-luks-cbc-essiv.qcow2
cp enc-aes.qcow2.gz enc-luks-cbc-essiv.qcow2.gz qemu-encrypted.sha256 "$out"
ls -l enc-*.qcow2.gz
