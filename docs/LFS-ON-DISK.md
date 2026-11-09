# 4.4BSD LFS on disk (NetBSD, version 2, 32-bit)

LFS is the log-structured file system of 4.4BSD (Rosenblum and Ousterhout's design,
Seltzer's BSD implementation), still maintained in NetBSD. This page is the specification
`FileSystem.Lfs` is written from.

**Sources (rung 1).** NetBSD's BSD-licensed `sys/ufs/lfs/lfs.h` (on-disk structures) and
`lfs_cksum.c` (superblock checksum), which the reader converts; `sbin/newfs_lfs/make_lfs.c`
(volume geometry) and `sbin/fsck_lfs/segwrite.c`, `pass0.c`, `pass5.c` (the userland
segment writer and the bookkeeping `fsck_lfs` checks), which the writer converts (notice:
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

## Segments, partial segments and the checkpoint

The volume after the 8 KiB label is cut into `nseg` segments of `fsbpseg` fragments;
segment *n* starts at `n × fsbpseg + s0addr` (`s0addr` is 0, so segment 0 also holds the
label and the first superblock). A segment flagged `SUPERBLOCK` in the usage table starts
with an 8 KiB superblock slot. Data is only ever appended at the head of the log
(`dlfs_offset`, inside `dlfs_curseg`), one **partial segment** at a time:

| Part | Content |
| --- | --- |
| summary (`sumsize` = fragment size) | `SEGSUM`, then one `FINFO` per file, then the inode-block addresses counted back from the summary's end |
| file blocks | in the order the `FINFO`s list them; a file's last block is a fragment when it is a direct block |
| inode blocks | `ibsize` bytes, `inopb` inodes each |

`SEGSUM` (48 bytes): summary checksum (0), data checksum (4), magic `0x061561` (8), next
segment (12), roll-forward ident (16), `nfinfo` (20, u16), inode count (22, u16), flags (24,
u16; userland writes `SS_RFW` 0x08), serial (32, u64), creation time (40, u64). `FINFO`:
block count, inode version, inode number, size of the last block, then the logical block
numbers. Indirect blocks carry negative numbers: the single-indirect block is −12, the
double-indirect block −(12 + nindir + 1), its *c*-th child −(12 + nindir) − c × nindir.
The data checksum XORs the 16-bit halves of the first 32-bit word of every block in disk
order; the summary checksum XORs the 16-bit words of the summary from byte 4 to its end.

The IFILE starts with the cleaner information (clean and dirty segment counts, `bfree`,
`avail`, free-inode list head and tail) and the segment usage table, 24 bytes per segment:
live bytes, `nsums` (summaries), `ninos` (inode blocks), flags (`ACTIVE` 1, `DIRTY` 2,
`SUPERBLOCK` 4, `EMPTY` 0x10) and the last write time. A **checkpoint** is a superblock
carrying `idaddr`, the log head (`offset`, `curseg`, `nextseg`, `lastpseg`), the space
totals and the serial of the last summary written; checkpoints alternate between the first
two superblock slots.

`fsck_lfs` holds a volume to these rules, and the writer keeps them:

- a segment's live bytes are the sizes of the live blocks in it plus 128 per live inode
  whose newest copy is in it — the IFILE's own inode included;
- `nclean` counts the segments without `DIRTY`; `dmeta` sums `sumsize × nsums` and
  `ibsize × ninos` (in fragments) over the dirty ones;
- `avail` = clean segments − 8 KiB for each clean superblock segment − the label when
  segment 0 is clean + the rest of the current segment − (minfreeseg − minfreeseg / 2)
  segments; `bfree` lies between `dsize − label − used` with and without inode blocks;
- free inodes form one list through the IFILE's next-free fields, from the cleaner head to
  its tail, holding every entry above 2 whose address is 0 and nothing else; the list is
  never empty (an IFILE block of fresh entries is added first);
- nothing that looks like a valid summary may sit at the checkpoint's `offset`, or
  roll-forward would replay it.

## Geometry `newfs_lfs` chooses

Up to 65,536 sectors: 1 KiB blocks, 512-byte fragments, 32 KiB segments; larger volumes
8 KiB blocks, 1 KiB fragments, 1 MiB segments. `nseg` = (fragments − label) / `fsbpseg`;
`minfreeseg` = max(nseg / 10, 20); `resvseg` = max(minfreeseg / 2 + 1, 15). Fewer than
4 × minfreeseg segments halves the segment size and tries again. `dsize` = (nseg −
minfreeseg) segments less 8 KiB per superblock. Superblocks sit at the label's end and then
every max(nseg / 10, 5) segments, at most ten. The fresh log head points past the last
segment, so the first write takes segment 0. IFILE entries 1 and 2 (the IFILE, the root)
are in use, entries 3 onwards form the free list. The root is `IFDIR | 0755` with two links.
A 16 MiB volume gets `dsize` 29,280, 511 segments, `minfreeseg` 51, superblocks every 51
segments.

## What the implementation does with it

It reads the newest valid checkpoint, then everything reachable from the root: modes,
owners, mtimes, hard links (one inode, several names), sparse files and indirect blocks.
Out-of-range addresses and malformed directory records are refused.

It writes the same way NetBSD's userland segment writer does. Create lays the volume out
with the geometry above, writes the files and directories, then the IFILE, and
checkpoints into every superblock slot. Add and Remove load the newest checkpoint, change
the tree in memory (a changed directory is rewritten whole; a removed file's inode returns
to the free list with its version bumped) and append the changed files, the changed
directories and the IFILE as new partial segments, then checkpoint. Nothing written before
is overwritten except the two checkpoint superblocks and the summary slot at the new log
head, which is cleared. Segments whose live bytes drop to zero go back to the clean pool, as
the cleaner would return them; a change that would use the segments the kernel keeps for
its cleaner is refused, so is a file needing triple-indirect blocks. Volumes whose inode
block or summary size is not the fragment size (`newfs_lfs -i`) or whose segment 0 does not
start at 0 are read but not edited.

**Proof.** In a NetBSD 11.1 guest, `fsck_lfs -n` reports nothing on our volumes (16 MiB
small geometry and 96 MiB default geometry); the kernel mounts them and its manifest
(modes, sizes, SHA-256) equals ours; the kernel then writes into them (new directory, a
rewrite, a delete, a multi-block file), `fsck_lfs -n` is still clean and our reader reads
the kernel's result; we edit that volume in place and NetBSD checks and mounts it once
more. `Compression.Tests/Lfs/LfsNetBsdTests.cs` (opt-in through `CWB_NETBSD_LIVE`).
