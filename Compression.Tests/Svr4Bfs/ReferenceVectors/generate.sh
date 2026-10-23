#!/bin/bash
# Regenerates kernel.bfs.gz + kernel.bfs.manifest: mkfs.bfs formats the volume, the Linux
# bfs driver (libguestfs appliance) writes every file, and the manifest is what that
# driver reads back. Run inside WSL/Linux with util-linux and libguestfs-tools.
set -e
export LIBGUESTFS_BACKEND=direct
echo 1234 | sudo -S chmod 0666 /dev/kvm 2>/dev/null || true
echo 1234 | sudo -S chmod 0644 /boot/vmlinuz-* 2>/dev/null || true
out="${1:?usage: generate.sh <output-dir>}"
mkdir -p $out; d=$(mktemp -d)
truncate -s 64K $d/kernel.bfs
mkfs.bfs -V kvol01 -F kfs001 $d/kernel.bfs
seq 1 600 > $d/numbers.txt
head -c 1300 /dev/urandom > $d/random.bin
printf 'gone soon' > $d/doomed.txt
cat > $d/s.txt <<EOF
run
mount-vfs "" bfs /dev/sda /
upload $d/numbers.txt /numbers.txt
upload $d/random.bin /random.bin
upload $d/doomed.txt /doomed.txt
touch /empty
write /hello.txt "hello from the kernel"
ln /hello.txt /hello-link
rm /doomed.txt
chmod 0600 /random.bin
ls /
umount /
EOF
timeout 600 guestfish -a $d/kernel.bfs -f $d/s.txt
printf 'run\nmount-vfs "" bfs /dev/sda /\ntar-out / %s\numount /\n' $d/k.tar > $d/r.txt
timeout 600 guestfish -a $d/kernel.bfs -f $d/r.txt
mkdir $d/x; tar -xf $d/k.tar -C $d/x; (cd $d/x && sha256sum * && ls -la)
cp $d/kernel.bfs $out/
(cd $d/x && sha256sum *) > $out/kernel.bfs.manifest
gzip -9 -n -f $out/kernel.bfs
