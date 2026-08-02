# ext2/3/4 (`Ext`)

ext2/ext3/ext4 Linux filesystem image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.ext2` |
| Recognised extensions | `.ext2`, `.ext3`, `.ext4`, `.img` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `53 EF` | 1080 | 0.80 |

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

By moving what is out of place, through `ExtBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### ExtFormatDescriptor

References:

### ExtReader

Reads ext2/ext3/ext4 filesystem images. Parses the superblock, block group descriptors, inode table, and directory entries. Supports both direct/indirect block pointers (ext2/3) and extent trees (ext4).

### ExtWriter

Builds minimal ext2 filesystem images from scratch. Uses 1024-byte blocks by default with a single block group. Files are stored using direct block pointers.

Produces fsck-clean output: free-block/free-inode counts, used-dirs count, inode link counts, inode i_blocks (sector tally), and all three inode timestamps are populated so that dumpe2fs / e2fsck do not report inconsistencies.

### ExtExtentMap

Walks an ext2/3/4 image and yields its actual on-disk byte layout — per-file extent runs (one `DefragBlockInfo` per contiguous block range) plus metadata regions (superblock, group descriptors, block + inode bitmaps, inode table). Used by the defragment window's block-map preview.

Streaming: never loads the whole image. All reads flow through a `SectorCache` so multi-TB ext4 images (a 50 TB volume's BGD table + bitmaps are tens of MB) work without OOM.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BlockSize` | Integer | `4096` | `1024`, `2048`, `4096` | Block Size (bytes) |
| `InodeSize` | Integer | `256` | `128`, `256` | Inode Size (bytes) |
| `Journal` | Boolean | `true` | any | Enable the journal (always on for ext3/ext4; ext2 has none). |
| `Version` | Enum | `ext4` | `ext2`, `ext3`, `ext4` | ext filesystem revision. ext3 adds journaling; ext4 adds extents + large file support. |
| `VolumeLabel` | String | `` | any | Volume Label |

## Storage methods

- `stored` — Stored

## Further reading

- https://docs.kernel.org/filesystems/ext4/index.html — the kernel's ext4 on-disk layout documentation (superblock, group descriptors, inodes, extents; ext2/3 are subsets)
- https://e2fsprogs.sourceforge.net/ext2intro.html — Card/Ts'o/Tweedie, "Design and Implementation of the Second Extended Filesystem"
- https://github.com/tytso/e2fsprogs — e2fsprogs, the canonical userspace implementation
- https://en.wikipedia.org/wiki/Ext4 — Wikipedia overview

