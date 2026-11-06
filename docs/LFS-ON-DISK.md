# 4.4BSD LFS on disk (NetBSD, version 2, 32-bit)

LFS is the log-structured file system of 4.4BSD (Rosenblum and Ousterhout's design,
Seltzer's BSD implementation), still maintained in NetBSD. This page is the specification
`FileSystem.Lfs` is written from.

**Sources (rung 1).** NetBSD's BSD-licensed `sys/ufs/lfs/lfs.h` (on-disk structures) and
`lfs_cksum.c` (superblock checksum), which the reader converts (notice:
`THIRD-PARTY-NOTICE.Lfs.txt`). All integers are little-endian. NetBSD also writes a 64-bit
variant (`LFS64_MAGIC`) and byte-swapped volumes; both are refused.

## Addresses

A disk address (`daddr`) counts fragments. Its byte offset is
`(daddr << dlfs_fsbtodb) × 512`. `newfs_lfs` on a 24 MiB volume chooses 1 KiB blocks,
512-byte fragments and 32 KiB segments. With `-b 4096 -f 4096`, `fsbtodb` is 3.

## Superblock (512 bytes; the first copy at byte 8192)

| Offset | Field |
| --- | --- |
| 0 | magic `0x070162` |
| 4 | version, 2 |
| 20 / 24 / 28 | block size / fragment size / fragments per block |
| 52 | `idaddr`: disk address of the IFILE's inode block |
| 56 | inode number of the IFILE (1) |
| 100 / 104 / 112 | inodes per block / IFILE entries per block / pointers per indirect block |
| 124 / 128 | cleaner-info blocks / segment-table blocks at the head of the IFILE |
| 184 | longest symlink kept inside the inode (60) |
| 188 | ten superblock addresses (`sboffs`) |
| 336 | serial number of the checkpoint |
| 344 | inode block size |
| 360 | inode format |
| 372 | `fsbtodb` |
| 508 | checksum: XOR of the 254 little-endian 16-bit words before it |

Checkpoints alternate between the superblock copies. The valid copy with the highest
serial holds the newest one, which is what the kernel mounts. Partial segments written
after it are left for roll-forward and are not read here. In the fixture, `fsck_lfs -y`
rolled them forward into a checkpoint first.

## Inodes and the IFILE

Inodes are 128 bytes:

| Offset | Field |
| --- | --- |
| 0 | mode |
| 4 | inode number |
| 8 | size (64-bit) |
| 24 | mtime |
| 40 | 12 direct pointers |
| 88 | single, double and triple indirect pointers |
| 104 | blocks held |
| 112 / 116 | uid / gid |

An inode block (`ibsize`) holds several inodes in any order. An inode is found by its
inode-number field.

The IFILE (inode 1) is an ordinary file. It starts with `cleansz` blocks of cleaner
information and `segtabsz` blocks of segment usage, then holds 20-byte entries: version,
**daddr of the block holding the inode's newest copy**, next-free and atime. Entry *n* is
in IFILE block `cleansz + segtabsz + n / ifpb`. An address of 0 means the inode is free.

## Files and directories

File blocks are reached the UFS way: 12 direct pointers, then single, double and triple
indirect blocks of `nindir` 32-bit addresses each. Pointer 0 is a hole. Directories are
512-byte chunks of records: inode (4), record length (2), type (1), name length (1), name.
A symlink shorter than 60 bytes that holds no blocks keeps its target in the pointer area.
The root is inode 2.

## What the implementation does with it

It reads the newest valid checkpoint, then everything reachable from the root: modes,
owners, mtimes, hard links (one inode, several names), sparse files and indirect blocks.
Out-of-range addresses and malformed directory records are refused. Read-only: a writer
must pass `fsck_lfs` and mount in the guest, and that has not been built yet.
