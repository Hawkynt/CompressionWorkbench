# UFS reference volumes

Volumes written by third-party tools, checked in so the reader tests run without
BSD or Linux tools. `generate.sh` documents how they were made; it is not a build input.

| File | Made by | What it is |
|------|---------|------------|
| `makefs-ufs1-le.img.gz` | `makefs -t ffs -o version=1,bsize=4096,fsize=512 -B le -s 4m` (NetBSD makefs, Ubuntu package `makefs` 20190105-3) | UFS1, little-endian, superblock at 8192, holding the tree in `manifest.txt`. |
| `makefs-ufs1-be.img.gz` | same, `-B be` | UFS1, big-endian. |
| `makefs-ufs2-le.img.gz` | same, `version=2` | UFS2, little-endian. This makefs puts the UFS2 superblock at 8192 (`fs_sblockloc` = 8192), which FreeBSD's `SBLOCKSEARCH` also finds. |
| `makefs-ufs2-be.img.gz` | same, `version=2 -B be` | UFS2, big-endian. |
| `newfs-ufs2.img.gz` | FreeBSD 8.2 `newfs -O2 -L CWBUFS2` (Debian `ufsutils_8.2-3_amd64.deb`, `mkfs.ufs`) on an 8 MiB loop device | An empty UFS2 volume, superblock at `SBLOCK_UFS2` (65536), four cylinder groups, 16 KiB blocks. |
| `manifest.txt` | `generate.sh` | SHA-256 and path of every file in the source tree; `L` lines are symbolic links and their targets, `D` lines directories. |

The tree covers nested and empty directories, an empty file, a 2.7 MB file that
needs the double-indirect tree with 4 KiB UFS2 blocks, a hard link, a 255-byte
name, a UTF-8 name, and symbolic links either side of both fast-symlink limits
(59/60 bytes for UFS1, 119/120 for UFS2). Every timestamp is 2024-02-29 12:34:56 UTC.

The little-endian makefs volumes and the newfs volume passed FreeBSD 8.2
`fsck_ffs -n` (`fsck.ufs` from the same ufsutils package); that fsck build reads
only the host byte order, so the big-endian volumes are checked by content alone.
The newfs volume also mounts under the Linux kernel's ufs driver
(`ufstype=ufs2`, kernel 6.8 in the libguestfs appliance).

Captured 2026-10-26 on Ubuntu 24.04 under WSL2. Compressed with `gzip -9 -n`.

A larger oracle that is not checked in: partition `a` of
`FreeBSD-14.5-RELEASE-amd64-mini-memstick.img` (UFS2 written by FreeBSD's own
makefs, 665 MiB, 26 777 files, 1 246 symbolic links), whose files the Linux kernel
copied out. `UfsReferenceVectorTests.GivenAFreeBsdReleaseVolume_…` compares the
reader against that copy when `CWB_UFS2_FREEBSD_IMAGE` and `CWB_UFS2_FREEBSD_MANIFEST`
point at the carved partition and its `sha256sum` manifest.
