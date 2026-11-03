# SVR4 / UnixWare Boot File System (BFS) on disk

The UnixWare `/stand` volume: one flat directory and contiguous files, built so a boot
loader can find and read a kernel without much code. This page is the specification
`FileSystem.Svr4Bfs` is written from.

**Sources.** Structure layouts and constants come from the Linux UAPI header
`include/uapi/linux/bfs_fs.h`; constants are not expression. Every behaviour below was
**measured**, not read from code: `mkfs.bfs` from util-linux 2.39.3 formatted the volumes,
and the Linux 6.8 `bfs` driver (in a libguestfs appliance) wrote into them. The
measurements are reproducible with
`Compression.Tests/Svr4Bfs/ReferenceVectors/generate.sh`, and that volume is a test fixture.

## Geometry

Everything is little-endian and addressed in 512-byte blocks.

| Blocks | Contents |
| --- | --- |
| 0 | superblock |
| 1 .. `s_start/512 - 1` | inodes, 64 bytes each, 8 per block |
| `s_start/512` .. `s_end/512` | data: the root directory and every file, each one contiguous run |

## Superblock (block 0)

| Offset | Size | Field | Value / meaning |
| --- | --- | --- | --- |
| 0 | 4 | `s_magic` | `0x1BADFACE` |
| 4 | 4 | `s_start` | byte offset of the first data block (end of the inode area) |
| 8 | 4 | `s_end` | byte offset of the **last** byte of the volume (size − 1) |
| 12 | 4 | `s_from` | compaction source; `0xFFFFFFFF` when clean |
| 16 | 4 | `s_to` | compaction target; `0xFFFFFFFF` when clean |
| 20 | 4 | `s_bfrom` | compaction backup; `0xFFFFFFFF` |
| 24 | 4 | `s_bto` | compaction backup; `0xFFFFFFFF` |
| 28 | 6 | `s_fsname` | file-system name, padded with spaces (`mkfs.bfs -F`) |
| 34 | 6 | `s_volume` | volume name, padded with spaces (`mkfs.bfs -V`) |
| 40 | 472 | padding | zero |

Measured: a 64 KiB `mkfs.bfs` volume has `s_start = 0xE00` (48 inodes in 6 blocks),
`s_end = 0xFFFF`; with `-N 100` the inode count rounds up to 104 (13 blocks,
`s_start = 0x1B00`). The highest inode number is `(s_start − 512) / 64 + 1`.

A volume whose `s_from`/`s_to` are not all ones records an interrupted compaction; the
editor refuses it.

## Inode (64 bytes)

Inode *n* lives at byte `512 + (n − 2) × 64`; inode 2 is the root directory.

| Offset | Size | Field | Meaning |
| --- | --- | --- | --- |
| 0 | 2 | `i_ino` | the inode's own number; **0 = free slot** |
| 2 | 2 | `i_unused` | — |
| 4 | 4 | `i_sblock` | first block; **0 for a file with no data** |
| 8 | 4 | `i_eblock` | last block (inclusive) |
| 12 | 4 | `i_eoffset` | byte offset in the volume of the file's last byte |
| 16 | 4 | `i_vtype` | 1 = regular file, 2 = directory |
| 20 | 4 | `i_mode` | full mode word incl. type bits (`0x41ED` root, `0x81A4` file) |
| 24 | 4 | `i_uid` | |
| 28 | 4 | `i_gid` | |
| 32 | 4 | `i_nlink` | |
| 36 | 4 | `i_atime` | Unix seconds |
| 40 | 4 | `i_mtime` | |
| 44 | 4 | `i_ctime` | |
| 48 | 16 | padding | |

Size = `i_eoffset + 1 − i_sblock × 512`, or 0 when `i_sblock` is 0. Measured: the driver
writes an empty file as `i_sblock = i_eblock = 0`, `i_eoffset = 0xFFFFFFFF`.

`mkfs.bfs` writes the root as blocks 7..8 (two blocks) on a 48-inode volume, size 32
(`.` and `..`), mode `040755`, uid 0, **gid 1**, two links. Files the driver creates get
mode `0100644`, uid/gid of the caller, one link.

## Directory

Only the root directory exists; there are no subdirectories (`statvfs` reports
`namemax 14`). Entries are 16 bytes:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 2 | inode number; **0 = free slot** |
| 2 | 14 | name, NUL-padded; a 14-byte name has no terminator |

The directory's size covers up to its highest used slot. Measured: the driver never grows
the directory's block run — it looks for a free slot anywhere in `i_sblock..i_eblock` and,
when that slot lies past the size, extends the size by one entry. Unlinking clears the
inode number and leaves the name bytes.

## Allocation

Files are single contiguous runs. Measured: the driver places a new file right after the
last allocated block of the volume. Deleting a file zeroes its inode slot and leaves its
data blocks as they were, so holes appear; the fixture keeps one. Hard links are two
directory entries with the same inode number and `i_nlink` 2.

## What the implementation does with it

* **Read** — any clean or unclean volume; a name whose inode is free, out of range, or
  whose extent leaves the volume is not listed (its bytes cannot be known).
* **Create** — the `mkfs.bfs` layout (byte-identical metadata for the same geometry, apart
  from timestamps), files contiguous behind a root directory that has a slot for every
  inode, because the kernel cannot grow it later.
* **Edit in place** — first-fit contiguous allocation (holes included), free inode slot,
  free directory slot; removal clears the slot and, with the last link, zeroes the inode
  and the data blocks. A name over 14 bytes, a directory, no contiguous run, no free inode
  or directory slot, or an interrupted compaction is refused with the image unchanged.
