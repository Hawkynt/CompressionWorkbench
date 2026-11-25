# Tux3 on disk (revision 2015-06-01)

Tux3 is Daniel Phillips' and OGAWA Hirofumi's versioning filesystem for Linux. This page describes
the layout the linux-tux3 tree (`fs/tux3`, branch `hirofumi-user`) writes with its magic
`tux3` `20 15 06 01`, as far as `FileSystem.Tux3` reads and writes it. Tux3 is GPL: its sources
were read as reference material to write this page, and its user tools (`tux3`, `tux3fuse`) and its
kernel (branch `hirofumi`, see "Kernel oracle") were run as black boxes to make and check the
reference volumes; no code was copied, converted or transliterated (sourcing rung 2). Constants (magic numbers, record codes and sizes, attribute numbers and sizes)
are copied exactly.

All integers are big-endian. Block numbers count blocks of `1 << blockbits` bytes from the start
of the volume.

## Superblock (byte 4096)

| Offset | Size | Field |
| ---: | ---: | --- |
| 0x00 | 8 | magic `t u x 3 0x20 0x15 0x06 0x01` |
| 0x08 | 8 | creation date |
| 0x10 | 8 | flags |
| 0x18 | 2 | blockbits (12 = 4 KiB blocks) |
| 0x1A | 6 | unused |
| 0x20 | 8 | volume size in blocks |
| 0x28 | 8 | inode tree root (packed root, below) |
| 0x30 | 8 | orphan tree root |
| 0x38 | 8 | used inode numbers |
| 0x40 | 8 | next block (allocation hint) |
| 0x48 | 8 | atom dictionary size |
| 0x50 | 4 | free atom |
| 0x54 | 4 | atom generation |
| 0x58 | 8 | newest log block |
| 0x60 | 4 | number of log blocks in the chain |

The superblock is rewritten at every commit ("delta"), so the tree roots and the log chain in it are
current.

A **packed root** is a 64-bit word: bit 63 *direct*, bits 48-62 a count, bits 0-47 a block. When
*direct* is clear the count is the tree's depth (levels including the leaf level; 0 = empty tree)
and the block is the root node. When *direct* is set (data trees only) the file is one extent: the
block is its first physical block and the count is its length in blocks.

## B-tree nodes

An index node ("bnode") starts with magic `0xb4de` and four unused bytes. A dictionary follows at
byte 6: a 16-bit entry count, then the 64-bit keys growing up from byte 8, while the 64-bit child
block numbers grow down from the end of the block (child *i* at `blocksize - 8 * (i + 1)`). Child
*i* holds the keys from key *i* up to key *i+1*; the first key is not used.

Inode-tree leaves ("ileaf", magic `0x90de`): magic, a 16-bit entry count, 4 bytes padding, the
64-bit base inode number, then *count* 16-bit offsets. Inode `base + i` has the attributes that end
`offset[i]` bytes before the end of the block and start `offset[i-1]` bytes before it (`offset[-1]`
is 0); an inode whose two offsets are equal is absent.

Data-tree leaves ("dleaf", magic `0xda7a`): magic, a 16-bit extent count, 4 unused bytes, then
16-byte extents: bits 0-47 of the first word the logical block, bits 0-47 of the second the
physical block (the top 16 bits of both are a version, always 0 here). An extent runs from its
logical block to the next extent's; physical block 0 is a hole, and the last extent is always such
a sentinel, marking where the leaf's range ends.

## Inode attributes

An inode's attribute bytes are records of a 16-bit head (attribute kind in the top 4 bits, version
in the low 12, always 0) followed by the attribute:

| Kind | Size | Contents |
| ---: | ---: | --- |
| 0 mode/owner | 10 | 16-bit mode, 32-bit uid, 32-bit gid |
| 1 ctime/size | 16 | 64-bit ctime (nanoseconds), 64-bit size |
| 2 link count | 4 | 32-bit count (absent: 2 for a directory, 1 otherwise) |
| 3 mtime | 8 | 64-bit mtime (nanoseconds; absent: same as ctime) |
| 4 data tree | 8 | packed root of the data tree (absent: empty) |
| 9 generic | 8 | device number of a device node (`major << 8 \| minor` while both fit in 8 bits, else `minor & 0xFF \| major << 8 \| (minor & ~0xFF) << 12`), parent inode of a directory |
| 11 immediate data | 2 + n | 16-bit length *n*, then *n* bytes |
| 12 extended attribute | 2 + n | 16-bit length *n*, a 16-bit atom, then *n − 2* bytes |

Inode 63 is the root directory; 1 to 4 and 61, 62 are internal (allocation bitmap, group counts,
version and atom tables, the volume and log maps). Inodes from 64 are files.

## Files and directories

A file's bytes are its data tree's blocks, by logical block number, cut to its size; a hole reads as
zeros. A symlink's target is its data, like a file's. A directory's data blocks hold entries like
ext2: a 64-bit inode number, a 16-bit record length, an 8-bit name length, an 8-bit file type (1
file, 2 directory, 3 character device, 4 block device, 5 FIFO, 6 socket, 7 symlink), then the name;
a record length is the entry rounded up to 8 bytes, an entry never crosses a block, and the last
record of a block reaches its end; a name length of 0 marks a free record. There are no `.` and `..`
entries on disk. A directory's size is its blocks times the block size (an empty one has size 0
and no data tree); its link count is 2 plus its subdirectories and is stored only when it is not 2
(a file's only when it is not 1); the mtime is stored only when it differs from the ctime.

## The log

Tux3 writes a changed leaf or data block to a new place on every commit and writes the leaf there,
but it does not rewrite the index nodes above it at once: their changes are logged, and only an
occasional *unify* writes the index nodes themselves. A reader that walks the trees must therefore
first apply the log's index changes to the nodes it reads.

The superblock names the newest log block and the chain's length; each log block (magic `0x10ad`,
16-bit byte count, 4 unused bytes, the 64-bit previous log block) holds records of a code byte
followed by fixed-size arguments (48-bit block numbers and keys, 16- and 32-bit counts):

| Code | Record | Bytes | Arguments |
| ---: | --- | ---: | --- |
| 0x33 | BALLOC | 11 | count:32, block:48 |
| 0x34 | BFREE | 11 | count:32, block:48 |
| 0x35 | BFREE_ON_UNIFY | 11 | count:32, block:48 |
| 0x36 | BFREE_RELOG | 11 | count:32, block:48 |
| 0x37 | LEAF_REDIRECT | 13 | old:48, new:48 |
| 0x38 | LEAF_FREE | 7 | block:48 |
| 0x39 | BNODE_REDIRECT | 13 | old:48, new:48 |
| 0x3A | BNODE_ROOT | 26 | count:8, root:48, left:48, right:48, right key:48 |
| 0x3B | BNODE_SPLIT | 15 | position:16, source:48, destination:48 |
| 0x3C | BNODE_ADD | 19 | parent:48, child:48, key:48 |
| 0x3D | BNODE_UPDATE | 19 | parent:48, child:48, key:48 |
| 0x3E | BNODE_MERGE | 13 | source:48, destination:48 |
| 0x3F | BNODE_DEL | 15 | count:16, node:48, key:48 |
| 0x40 | BNODE_ADJUST | 19 | node:48, from key:48, to key:48 |
| 0x41 | BNODE_FREE | 7 | block:48 |
| 0x42 | ORPHAN_ADD | 9 | version:16, inode:48 |
| 0x43 | ORPHAN_DEL | 9 | version:16, inode:48 |
| 0x44 | FREEBLOCKS | 7 | free blocks:48 |
| 0x45 | UNIFY | 1 | — |
| 0x46 | DELTA | 1 | — |

The chain is read oldest first. Records before the newest UNIFY mark are already in the trees.
From that mark on, the index-node records are applied in order to a working copy of the nodes:

| Record | Effect on the nodes |
| --- | --- |
| BNODE_REDIRECT | the node at *new* is a copy of the node at *old* |
| BNODE_ROOT | the node at *root* is new: *count* children, *left* (key 0) and *right* (key *right key*) |
| BNODE_SPLIT | the entries of *source* from *position* on move to a new node at *destination* |
| BNODE_ADD | the child and its key are inserted into *parent* after the last entry whose key is not greater |
| BNODE_UPDATE | the entry of *parent* with that key now points to *child* |
| BNODE_MERGE | the entries of *source* are appended to *destination* |
| BNODE_DEL | *count* entries are removed from *node*, starting at the entry with that key |
| BNODE_ADJUST | the key *from* in *node* becomes *to* |

The allocation records (BALLOC, BFREE…, LEAF_REDIRECT, the log blocks themselves) change the
allocation bitmap, not what a reader sees.

## Writing

`tux3 mkfs` leaves almost nothing in the trees: the inode tree is one leaf (base 4) holding inode 4
(mode 0, size 2^60) and the root, and the allocation is not in any bitmap but in the log — `BALLOC 2
@ 0`, `DELTA`, `BALLOC 1 @ 2` in one log block that is not itself allocated; the kernel builds the
bitmap and group counts when it replays the log at mount. The internal inodes 1–3, 61 and 62 do not
exist until the kernel first writes. The writer produces the same shape:

* block 1 the superblock (log chain and count, inode-tree root, `usedinodes` one past the highest
  inode, `nextblock` past the last block used, atom generation 1);
* from block 2, every directory's entry blocks and every file's and symlink's bytes, each in one
  contiguous run named by a direct data-tree root (a data leaf with the run and its sentinel when a
  run is longer than the root's 15-bit count);
* inode-tree leaves (base 4, then each leaf's first inode), under one index node when there are
  several;
* one log block (more if needed): `BALLOC 2 @ 0`, `DELTA`, then one `BALLOC` for every block from 2
  to the last.

Inodes are numbered from 64 in the order objects are added; every object gets the volume's creation
time as ctime.

## Kernel oracle

The kernel is linux-tux3, branch `hirofumi`, commit `e1c4179dd161` (Linux 5.16 with `fs/tux3`;
`codeload.github.com/OGAWAHirofumi/linux-tux3/tar.gz/e1c4179dd161`, SHA-256 `edc25418…958f7b8bd6`),
configured from `defconfig` plus `kvm_guest.config` with `CONFIG_TUX3` and virtio built in, and
built with gcc 12 (`make -j2 bzImage`). Two build-only changes let a 2021 kernel's host tools build
with today's toolchain: objtool's warnings are not errors, and objtool treats an object file
without a symbol table as having no symbols. `Compression.Tests/Tux3/KernelOracle/kernel-run.sh`
boots it under QEMU (1 GiB) with a busybox initramfs; `CWB_TUX3_BZIMAGE` names the image (default
`~/.cache/cwb/tux3-kernel/bzImage`). Nothing of it is in this repository; the kernel tests skip
without it. It mounts volumes of the user tools and of this writer, writes through the page cache
(files of any size, holes, devices, renames), and logs a warning on every non-waiting sync that
says nothing about the volume.

## What the implementation does with it

`Tux3Reader` reads revision 2015-06-01 volumes completely: it replays the log's index-node records
over the nodes it reads, walks the inode tree, and lists every directory, file, symlink and hard
link below the root with mode, owner, size, times and link count; it extracts file bytes (holes as
zeros) and symlink targets. Anything that does not parse is refused (`InvalidDataException`), never
guessed. The 2014-05-06 and 2012-12-20 revisions keep their metadata-only view. `Tux3Writer`
creates new volumes as described under "Writing"; volumes are not edited in place.

Checked against volumes the tux3 user tools made: `tux3 mkfs`, files written by `tux3 write`, a hard
link and a symlink by `tux3 link` and `tux3 symlink`, and a directory tree (a 150-entry directory
among it, whose index changes were still in the log) written through `tux3fuse`, each before and
after `tux3 unify`. Every entry matches what `tux3fuse` lists and reads back from the same image.
The tools are not dependable writers (`tux3fuse` aborts on writes of 4 KiB and more; `tux3 write`
leaves larger files at size 0), so the reference volumes keep to what they store reliably, and no
writer was offered until the kernel was built. Now a volume the kernel filled
(`kernel.img.gz`: files of 0 to 200 705 bytes with a hole, a 255-byte name, nested and empty
directories, a 300-entry directory, symlinks, a hard link, a FIFO and two device nodes, an owner and a
2001 mtime, index changes still in the log) reads back exactly as the kernel lists and reads it; the
kernel mounts every volume the writer makes — every object kind, direct roots and data leaves, a
400-entry directory over several inode leaves — and reads back each object byte for byte; and after
the kernel adds, appends to, links, renames and removes objects in a writer volume, the reader sees
exactly what the kernel reads back.
