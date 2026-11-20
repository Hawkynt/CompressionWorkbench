# AXFS on disk

AXFS (Advanced XIP File System) is a read-only filesystem for embedded Linux. Numonyx
released it in 2008; it was proposed for mainline but never merged. A file's pages are
stored one of three ways: in place for execute-in-place (XIP), uncompressed in a
byte-aligned region, or zlib-compressed. This page is the specification
`FileSystem.Axfs` is written from.

**Sources (rung 2).** The GPL-2.0 sources at <https://github.com/jaredeh/axfs> were read to
write this page: `mkfs.axfs-legacy/mkfs.axfs.c` with `linux/axfs.h`, plus
`kernel/fs/axfs/axfs_fs.h`, `axfs_inode.c` and `axfs_super.c`. No code from them is used.
The facts below were then confirmed against images `mkfs.axfs` built: the reference
vectors in `Compression.Tests/Axfs/ReferenceVectors` read back byte-identical to their
source trees.

## Superblock (offset 0, all integers big-endian)

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | magic `0x48A0E4CD` |
| 4 | 16 | signature `Advanced XIP FS\0` |
| 20 | 40 | digest (SHA-1 field; `mkfs.axfs` leaves it zero) |
| 60 | 4 | `cblock_size`: largest uncompressed cblock (4096 from `mkfs.axfs`) |
| 64 | 8 | `files`: inode count |
| 72 | 8 | `size`: image size |
| 80 | 8 | `blocks`: node count |
| 88 | 8 | `mmap_size` |
| 96 … 232 | 8 each | offsets of the region descriptors, in this order: strings, xip, byte_aligned, compressed, node_type, node_index, cnode_offset, cnode_index, banode_offset, cblock_offset, inode_file_size, inode_name_offset, inode_num_entries, inode_mode_index, inode_array_index, modes, uids, gids |
| 240 | 3 | version major, minor, sub (2.2.0 from the legacy `mkfs.axfs`) |
| 243 | 1 | compression type, 0 = zlib |
| 248 | 8 | build timestamp (UNIX seconds; `mkfs.axfs` writes 0). The field is 8-byte aligned, which leaves 244–247 unused |
| 256 | 1 | `page_shift` (12 → 4096-byte nodes) |

The superblock ends at 257; `mkfs.axfs` zero-pads it to 264, where the region descriptors begin.
`size` covers the image up to and including a second copy of the magic, which the driver reads
from the last four bytes of `size` before it mounts; `mmap_size` equals `size`; `blocks` is the
number of nodes the files use.

## Region descriptor (40 bytes on disk, big-endian)

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 8 | `fsoffset`: start in the image |
| 8 | 8 | `size`: uncompressed bytes |
| 16 | 8 | `compressed_size`: non-zero means the region is one zlib stream of this many bytes inflating to `size` |
| 24 | 8 | `max_index`: entries in a byte table |
| 32 | 1 | `table_byte_depth`: bytes per byte-table entry |
| 33 | 1 | `incore` |

A **byte table** holds `max_index` entries of `table_byte_depth` bytes each. Entry *i* is
the big-endian number in bytes `i·depth … i·depth+depth−1`.

## Inodes

Inode 0 is the root. For inode *i*:

* mode = `modes[inode_mode_index[i]]`; uid and gid come from the same index into `uids`
  and `gids`
* size = `inode_file_size[i]`. For a device node it holds the encoded device number
* name = the NUL-terminated string at `strings + inode_name_offset[i]`
* directory: `inode_num_entries[i]` children, inodes `inode_array_index[i]` onwards,
  sorted by name
* regular file or symlink: page *p* of the data is node `inode_array_index[i] + p`. A
  symlink's data is its target

## Nodes

For node *n*: type = `node_type[n]` (0 XIP, 1 compressed, 2 byte-aligned); idx = `node_index[n]`.

* **XIP**: one page at `xip.fsoffset + idx << page_shift`
* **Byte-aligned**: bytes at `byte_aligned + banode_offset[idx]`, at most a page.
  `mkfs.axfs` stores a page here when compression does not shrink it, and steps its
  offset by the page length plus one
* **Compressed**: cblock `c = cnode_index[idx]` is the zlib stream (zlib header and
  Adler-32) at `compressed + cblock_offset[c]`, up to `compressed + cblock_offset[c+1]`.
  `cblock_offset` carries one extra entry, the end of the last cblock. The page starts at
  `cnode_offset[idx]` inside the inflated cblock. The legacy `mkfs.axfs` compresses each
  page into its own cblock at level 9

The last page of a file is cut to the file size. `mkfs.axfs -a` forces the pages of
owner-executable ELF files into the XIP region (measured: only full pages; the partial
last page is stored like any other); other pages are compressed or
byte-aligned. Files with the same content do not share nodes: in the reference images
`etc/numbers.txt` and its copy `deep/a/b/c/copy.txt` each have four nodes of their own.

## Layout as `mkfs.axfs` writes it

Measured on the reference images:

* the 18 region descriptors back to back from offset 264, in superblock order;
* the byte tables packed back to back from offset 984, in descriptor order from `node_type`
  to `gids`, with no alignment;
* zero padding to the next page, where the XIP region starts, then the byte-aligned
  region, the compressed region and the strings, each straight after the last;
* the end magic, then zero padding to a whole page.

Inodes are numbered breadth-first: each directory's children take consecutive inode
numbers, sorted by name, and the root's name in the strings is `./`. A directory's
`inode_file_size` is the sum of its children's name lengths plus one each. Nodes are
handed out in inode order, so each file's pages are consecutive nodes. A file with no
data has no entries and an array index of 0. `mkfs.axfs` sizes some node tables a
few entries larger than the nodes it writes; the driver only reads entries files point at.

## What the implementation does with it

Reads directories, regular files, symlinks, modes and owners, and data from all three
node kinds. Every zlib stream's Adler-32 is checked, and every table index and region
range is bounds-checked: a damaged image is refused, never read as different bytes.
Images other than version 2.x or zlib are refused. Device nodes, FIFOs and sockets are
listed without data.

## Writing

`AxfsWriter` writes directories, regular files and symlinks in the layout above. It
differs from `mkfs.axfs` in three ways:

* nothing goes into the XIP region;
* each byte table uses the fewest bytes per entry that hold its largest value, where
  `mkfs.axfs` uses 1 for `node_type` and 4 for the rest;
* byte-aligned nodes are packed without the extra byte `mkfs.axfs` leaves after each.

Each page is deflated into its own zlib cblock at the best level, with `cblock_size` set
to the page size. A page whose zlib stream is not shorter than the page is stored
byte-aligned instead. The compressed bytes are this package's own deflate output, so
they differ from zlib's, and images are not byte-identical to `mkfs.axfs` output.

The driver refuses to mount a root with no entries. The writer still writes such an
image, which our reader reads as empty.

**Proof.** `Compression.Tests/Axfs/KernelOracle/axfs-run.sh` builds the AXFS driver
(jaredeh/axfs at a pinned revision) as a module for Debian buster's Linux 4.19. It boots
that kernel under QEMU and mounts an image as a block device. Two build-time edits are
applied to the module, both outside the format:

* the mmap fault path calls a page-lock helper 4.19 does not export, so the harness
  locks the page outright;
* block-device reads take a division remainder as the block number, so the harness takes
  the quotient.

With these edits the driver reads both `mkfs.axfs` reference images exactly as their
manifests describe. It reads our images back with every file's size and MD5, every mode,
owner and group, and every link target as written. The test tree needs 2- and 3-byte
table entries, compressed and byte-aligned nodes, and a 70,000-byte file.
