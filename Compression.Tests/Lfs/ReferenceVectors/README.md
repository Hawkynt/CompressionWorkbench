# LFS reference volume

`lfs.img.gz` is a 24 MiB LFS volume that NetBSD created and wrote; nothing in this
repository wrote it. It is gzipped only to keep the repository small.

| | |
| --- | --- |
| Created by | NetBSD 11.1 amd64 (`NetBSD-11.1-amd64-live.img.gz` from cdn.netbsd.org/pub/NetBSD/images/11.1, SHA512 checked against the published `SHA512` file), booted headless under qemu with `Compression.Tests/Support/NetBsdGuest/netbsd-guest.py` |
| Steps | `newfs_lfs -F` on a vnd over a 24 MiB file (version 2 LFS32, 1 KiB blocks, 512-byte fragments, 32 KiB segments); mounted and filled through the kernel with `pax` from a source tree; a file written then removed; another rewritten; a hard link added; a 588,895-byte file needing indirect blocks; `umount`; `fsck_lfs -y -f` (roll forward to a checkpoint) |
| Created | 2026-11-06 |
| `lfs.manifest` | taken by NetBSD from the volume mounted read-only afterwards: `F mode size sha256 path`, `D mode path`, `L mode path -> target` |

`random.bin` comes from `/dev/urandom`, so a rebuild changes the image and the manifest
together. `LfsNetBsdTests` repeats the check on fresh volumes, with the default geometry
and with 4 KiB blocks and fragments, when `CWB_NETBSD_LIVE` names the unpacked live
image.
