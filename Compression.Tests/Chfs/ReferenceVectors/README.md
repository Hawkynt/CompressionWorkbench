# CHFS reference images

NetBSD built both images; nothing in this repository wrote them. They are gzipped only to
keep the repository small.

| | |
| --- | --- |
| Built by | `makefs -t chfs` in NetBSD 11.1 amd64 (`NetBSD-11.1-amd64-live.img.gz` from cdn.netbsd.org/pub/NetBSD/images/11.1, SHA512 checked against the published `SHA512` file), booted headless under qemu with `Compression.Tests/Support/NetBsdGuest/netbsd-guest.py` |
| `chfs-nand` | `makefs -t chfs /tmp/chfs-nand.img /tmp/src`: NAND, 2048-byte pages, 128 KiB eraseblocks (the defaults) |
| `chfs-nor` | `makefs -t chfs -o mediatype=0,pagesize=512,eraseblock=16384 ...`: NOR, nine 16 KiB eraseblocks |
| Created | 2026-11-06 |
| `chfs.manifest` | the source tree as NetBSD saw it (`stat -f %Lp`, `cksum -a sha256`): `F mode size sha256 path`, `D mode path`, `L mode path -> target` |

The tree holds nested and empty directories, an empty file, a 108,894-byte file spread
over 2 KiB data nodes, random data, a symlink and non-default modes. `random.bin` comes
from `/dev/urandom`, so a rebuild changes the images and the manifest together.
`ChfsNetBsdTests` repeats the check against fresh `makefs` output when `CWB_NETBSD_LIVE`
names the unpacked live image.
