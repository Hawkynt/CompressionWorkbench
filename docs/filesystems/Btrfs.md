# Btrfs Filesystem Image (`Btrfs`)

Btrfs copy-on-write filesystem image with real chunk tree + CRC-32C metadata checksums

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.btrfs` |
| Recognised extensions | `.btrfs`, `.img` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `5F 42 48 52 66 53 5F 4D` | 65600 | 0.90 |

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

By moving what is out of place, through `BtrfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### BtrfsFormatDescriptor

References:

### BtrfsReader

Reads Btrfs filesystem images (single-device, non-RAID). Parses superblock, builds chunk map (logical-to-physical translation), traverses B-trees to enumerate files and extract uncompressed extents.

### BtrfsWriter

Writes spec-compliant Btrfs filesystem images. Every image contains a populated `sys_chunk_array` inside the superblock, a real chunk tree with three `CHUNK_ITEM` entries (`SYSTEM`, `METADATA`, `DATA`) that map every logical range used by the image to its physical offset, a dev tree with one `DEV_ITEM` for the single device, a root tree pointing at the FS tree, and an FS tree leaf holding inode / directory-index / inline extent-data items for every added file. All metadata blocks carry the 4-byte little-endian CRC-32C (Castagnoli) at byte offset 0 per the on-disk spec.

### BtrfsExtentMap

Walks a Btrfs image (single-device, non-RAID) and yields its actual on-disk byte layout. Targets the WORM-minimal writer profile: a single fs-tree leaf with INODE_ITEM + DIR_INDEX + (mostly inline) EXTENT_DATA items per file, plus a populated chunk tree for logical→physical translation. Inline extents surface as MetadataReserved (they live inside the metadata leaf); regular extents surface as Used runs.

Streaming: reads go through a `SectorCache` so a 50 TB Btrfs image needs only a few MB of working set, not 50 TB of RAM. Only the 4 KiB superblock + a handful of node-sized reads (chunk tree, root tree, fs-tree leaf) actually hit the disk.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Features` | String | `mixed-bg,no-holes` | any | Comma-separated feature list; only the listed defaults are currently supported. |
| `Label` | String | `` | any | Optional volume label. |
| `NodeSize` | Integer | `16384` | `4096`, `8192`, `16384`, `32768`, `65536` | B-tree node size in bytes. 16KB is the modern default. |
| `SectorSize` | Integer | `4096` | `4096` | Sector size — Linux mkfs.btrfs only supports 4096 today. |

## Storage methods

- `stored` — Stored

## Further reading

- https://btrfs.readthedocs.io/en/latest/dev/On-disk-format.html — official btrfs on-disk format documentation (superblock, chunk/root/fs trees)
- https://github.com/torvalds/linux/tree/master/fs/btrfs — mainline kernel implementation
- https://en.wikipedia.org/wiki/Btrfs — Wikipedia overview

