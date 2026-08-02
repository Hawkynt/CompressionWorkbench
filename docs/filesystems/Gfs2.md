# GFS2 (Global File System 2) (`Gfs2`)

GFS2 (Red Hat cluster filesystem) — read superblock + single-leaf root directory + inline-data files; create a fresh empty lock_nolock volume that fsck.gfs2 accepts.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.gfs2` |
| Recognised extensions | `.gfs2` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `01 16 19 70` | 65536 | 0.85 |

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
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `Gfs2BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Gfs2FormatDescriptor

GFS2 (Global File System 2) descriptor — Red Hat's cluster filesystem, mainline Linux since 2.6.19. We parse the superblock at offset 65536, surface block size + lock proto/table + UUID + master/root inode pointers, and walk the root inode's inline directory entries (single-leaf, di_height==0). For regular files with inline data (height==0) we extract the bytes. On-disk layout reverse-validated against real `mkfs.gfs2` output (gfs2-utils 3.5.1): the `gfs2_meta_header` is 24 bytes, the sb carries a reserved `__pad2` inum between master and root, and the `gfs2_dirent` header is 40 bytes. See `Gfs2ExternalConformanceTests` for the mkfs.gfs2 / fsck.gfs2 gate.

Creation (`Create`, `Gfs2Writer`) emits a fresh, empty standalone (lock_nolock, single-journal) volume — superblock, the fixed first resource group plus a second data resource group with a correct (multi-block) allocation bitmap, the master directory and its system inodes (jindex, per_node, inum, statfs, rindex, quota), a formatted 8 MB journal of clean unmount log headers, and the root directory — all sized so real fsck.gfs2 -n passes clean (exit 0). Supported size range 16–256 MB (single data resource group); the volume is empty, since populating it with files is out of scope.

Out of scope (multi-week effort each): writing files/directories, ExHash multi-leaf directories, multi-level block indirection (di_height &gt; 0), devices &gt; 256 MB (which gfs2-utils splits into several evenly-spaced resource groups), journal replay, cluster lock manager state, extended attributes. Magic: `mh_magic = 0x01161970` (BE u32) at the start of the superblock meta header. On disk at byte offset 65536 this serialises as `01 16 19 70`. Confidence 0.85 — well-known constant at a fixed offset, but GFS2 shares this magic with GFS1 at slightly different layouts, so we keep a small margin below the 0.9-0.95 reserved for formats with a structurally unique header. References:

### Gfs2Reader

Read-only GFS2 (Global File System 2) image walker. Mainline Linux since 2.6.19; Red Hat cluster filesystem. Big-endian on-disk. What we parse: What we deliberately skip (multi-week effort each): References:

### Gfs2Writer

Clean-room GFS2 (Global File System 2) image writer producing a minimal, empty, standalone (`lock_nolock`, single-journal) volume that real `fsck.gfs2` (gfs2-utils) accepts without errors.

The output mirrors the on-disk structures defined in the public Linux kernel header include/uapi/linux/gfs2_ondisk.h and the layout produced by mkfs.gfs2, reverse-validated byte-for-byte against a real reference image. Big-endian throughout, 4096-byte blocks.

What we emit (everything fsck.gfs2 requires for a clean volume):

Block-accounting fields (rg_free, rg_dinodes, the master statfs, the inum next-formal-number) are all computed from the real layout so check_statfs passes.

### Gfs2ExtentMap

Reads a GFS2 volume's resource-group bitmaps and reports which blocks are in use. GFS2 accounts for allocation two bits per block: 00 is free, and every other state (data, unlinked, dinode) means the block is live. What the bitmaps leave clear is exactly the free space — including the blocks a removed file used to occupy, which still hold its bytes.

The resource groups are found by walking the chain rather than the rindex: each rgrp header carries rg_skip, the distance to the next one, and the last carries zero. A group's bitmap covers its data blocks only — the header block and the RB blocks that follow it are structure and are always in use — so the number of those blocks is recovered from the same relation the writer used to size them.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `16 MB`, `32 MB`, `64 MB`, `128 MB`, `256 MB` | Total volume size (16–256 MB; a single data resource group). |
| `LockTable` | String | `` | any | Cluster lock-table name stamped into sb_locktable (empty for a standalone volume). |

## Storage methods

- `stored` — Stored

## Further reading

- Linux kernel fs/gfs2/ — include/uapi/linux/gfs2_ondisk.h
- Red Hat Cluster Suite / Resilient Storage Add-On documentation

