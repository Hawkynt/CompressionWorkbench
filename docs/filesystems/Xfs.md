# XFS (`Xfs`)

XFS filesystem image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.xfs` |
| Recognised extensions | `.xfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `58 46 53 42` | 0 | 0.95 |

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

By moving what is out of place, through `XfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### XfsFormatDescriptor

R/W descriptor for SGI XFS filesystem images ("XFSB" superblock magic) at `mkfs.xfs`-faithful defaults. References:

### XfsWriter

Writes a minimal XFS v5 filesystem image that `xfs_repair -n -f` accepts.

Each allocation group (AG) is laid out as:

`block 0: SB (sector 0), AGF (sector 1), AGI (sector 2), AGFL (sector 3) block 1: bnobt root (1 leaf covering the free extent) block 2: cntbt root (same key ordering by length) block 3: inobt root (1 leaf covering the root-inode chunk for AG 0; empty for AG 1+) block 4: root-inode chunk start (64 inodes × 256 B = 16 KiB = 4 blocks) — AG 0 only block 8+: free space (used for file data in AG 0)`

All v5 metadata blocks (SB, AGF, AGI, AGFL, btree blocks, dinodes) are stamped with CRC-32C using the Castagnoli polynomial. Big-endian for most on-disk fields; CRC fields are little-endian per XFS v5 convention.

Scope: nested directory trees using short-form (inline), single-block ("XDB3") and leaf-form ("XDD3" data blocks + a "XFS_DIR3_LEAF1" hash index) dir2 directories; extent-based file data in one BMBT record per file; no RMAP, no REFCOUNT, no quotas, no realtime volume, no sparse-inode feature, no node-form (da-btree) directories — the directory block size is enlarged so the largest directory's hash index fits in a single leaf block.

### XfsExtentMap

Walks an XFS image and yields its actual on-disk byte layout. Targets the WORM writer profile: per-AG superblock + AGF + AGI + AGFL + bnobt/cntbt/ inobt headers as MetadataReserved, plus per-file extents (BMBT_REC packed 128-bit format) as Used runs. For inodes whose data fork is in `local` (inline) format, the file content lives inside the inode itself and surfaces as MetadataReserved.

Streaming: never loads the whole image. All reads flow through a `SectorCache` so multi-TB XFS images (an XFS volume can span thousands of AGs) work without OOM.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | XFS volume label stored in sb_fname (max 12 ASCII chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://mirrors.edge.kernel.org/pub/linux/utils/fs/xfs/docs/xfs_filesystem_structure.pdf — "XFS Algorithms & Data Structures" — the on-disk specification
- https://github.com/torvalds/linux/tree/master/fs/xfs — Linux reference implementation
- https://en.wikipedia.org/wiki/XFS — Wikipedia article

