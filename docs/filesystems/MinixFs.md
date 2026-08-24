# Minix FS (`MinixFs`)

Minix file system image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.minix` |
| Recognised extensions | `.minix`, `.img` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `5A 4D` | 1048 | 0.80 |
| `7F 13` | 1040 | 0.80 |
| `8F 13` | 1040 | 0.80 |
| `68 24` | 1040 | 0.80 |
| `78 24` | 1040 | 0.80 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | yes | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | yes | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | yes | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `MinixFsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### MinixFsFormatDescriptor

R/W descriptor for Minix filesystem images (v1/v2/v3 superblock magics) — the ext-family ancestor used by the Minix teaching OS and early Linux. References:

### MinixFsReader

Reads Minix filesystem images (v1, v2, v3). Parses the superblock, inode table, and directory entries. Supports direct, single-indirect, double-indirect, and triple-indirect zone pointers (v3). V1/V2 support direct and single/double indirect.

### MinixFsWriter

Builds minimal Minix v3 filesystem images. Uses 1024-byte blocks and creates real directory inodes for every path component, so a file added as `"a/b/c.txt"` is stored under nested directories `a` and `a/b`. Files are stored using direct zone pointers (up to 7 direct zones per file = up to 7168 bytes with 1K blocks). Directories may span multiple zones: their fixed-size entries fill the 7 direct zones and then a single-indirect zone, allowing a directory to hold thousands of entries (7 + 1024/4 = 263 zones = 4208 entries with 1K blocks).

### MinixFsExtentMap

Walks a Minix filesystem image (v1/v2/v3) and yields the actual on-disk byte layout: the boot block + superblock + inode/zone bitmaps + inode table as a single `MetadataReserved` run, each directory's zones as `MetadataReserved` (they hold directory entries, not file payload), and each regular file's data zones as `Used` runs. Unclaimed zones are reported as `Free`.

This replaces the earlier synthetic layout that fabricated offset += size extents — those never matched the real zone offsets, so any consumer that zero-filled the gaps (e.g. the unused-space wiper) would have corrupted live file data, inode tables and bitmaps.

For a regular file whose data zones are contiguous, a single Used extent is emitted with the file's name as `FileName` so the wiper can locate the zone tip via a size lookup. A file split across non-contiguous zone runs emits one Used extent per run with a name that is deliberately absent from the size lookup, so tip trimming never misfires on a run that does not start at file offset zero.

## Storage methods

- `minixfs` — Minix FS

## Further reading

- https://github.com/torvalds/linux/blob/master/include/uapi/linux/minix_fs.h — canonical on-disk structures for all versions
- https://github.com/torvalds/linux/tree/master/fs/minix — Linux reference implementation
- Tanenbaum & Woodhull, "Operating Systems: Design and Implementation" — the original Minix FS design
- https://en.wikipedia.org/wiki/Minix_file_system — Wikipedia article

