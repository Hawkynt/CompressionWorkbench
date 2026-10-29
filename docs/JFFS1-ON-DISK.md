# JFFS version 1 on disk

JFFS is the Journalling Flash File System that Axis Communications wrote for Linux 2.4.
It was superseded by JFFS2 and removed from the kernel during 2.6. This page is the
specification `FileSystem.Jffs1` is written from.

**Sources (rung 2).** `mkfs.jffs.c` from mtd-utils (GPL; the copy kept in DSLinux) was read
for the layout and is the black-box oracle. Both reference images
(`Compression.Tests/Jffs1/ReferenceVectors`) read back byte-identical to their source tree.
No kernel driver remains to check the behaviour of a running file system, so that is not
claimed.

## Log

The image is one log of nodes. Each node is a 60-byte raw inode, then the name, then the
data; the name and the data are each padded to four bytes, and `mkfs.jffs` pads with
`0xFF`. Integers use the image's byte order: the host order by default, or `-a little|big`.
The magic `0x34383931` ("1984" on a little-endian image) tells which.

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | magic |
| 4 | 4 | ino (the root is 1) |
| 8 | 4 | pino: parent inode |
| 12 | 4 | version |
| 16 | 4 | mode |
| 20 / 22 | 2 / 2 | uid / gid |
| 24 / 28 / 32 | 4 each | atime / mtime / ctime |
| 36 | 4 | offset: where this node's data starts in the file |
| 40 | 4 | dsize: data bytes in this node |
| 44 | 4 | rsize: bytes of the file this node replaces |
| 48 | 1 | nsize: name length |
| 49 | 1 | nlink |
| 50 | 1 | flags: rename and deleted bits (bit field) |
| 51 | 1 | accurate: 0 = obsolete node |
| 52 | 4 | dchksum: byte sum of the data |
| 56 | 2 | nchksum: byte sum of the name (16-bit) |
| 58 | 2 | chksum: byte sum of these 60 bytes, taken with `accurate` and `chksum` as zero (16-bit) |

`mkfs.jffs` writes each file's data in nodes of at most 32 KiB, with consecutive offsets,
`rsize` 0 and increasing versions. A symlink's data is its target. A device node's data is
its device number.

## What the implementation does with it

* It scans the log at four-byte steps. Erased or unrecognised words are skipped, and so is
  a node whose header checksum fails, as the 2.4 driver ignored dirty space. Obsolete nodes
  (`accurate == 0`) are skipped too.
* A node with a bad name or data checksum is refused. So is a node marked deleted or
  renamed, a node that rewrites bytes (`rsize` ≠ 0 or a non-appending offset), and two
  nodes with one version. That is a running file system's history, and no reference is
  left to check a replay against.
* Inodes are joined to their parents through `pino`. The latest version's metadata and
  name win. An inode the root cannot reach is refused.
* Read-only, in either byte order.
