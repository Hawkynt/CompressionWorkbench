# HTFS (SCO High Throughput File System) (`Htfs`)

SCO HTFS — WORM writer + nested-directory reader.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.htfs` |
| Recognised extensions | `.htfs`, `.s5` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `5D D1 2F 01` | 512 | 0.85 |

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

By moving what is out of place, through `HtfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### HtfsFormatDescriptor

SCO HTFS (High Throughput File System) — S5-derived FS introduced in SCO OpenServer 5. Now exposes a WORM writer + reader with real nested subdirectories, defrag/purge/conversion, fileset optimizer, and an options schema (BlockSize / InodeCount / VolumeLabel). References:

Reference: SCO OpenServer Development System docs, sys/fs/htfs/htfs_fs.h. Superblock at byte offset 512 (sector 1). Magic 0x012FD15D (LE u32) at byte offset 0 of the superblock.

Hierarchy: real — directories nest via the writer's inode + 16-byte dirent chain (single-block dirs cap one BB of entries each).

### HtfsReader

Read-side companion to `HtfsWriter`. Walks the SB at sector 1, the inode array immediately after, and every directory body to surface the file tree at full nested paths.

### HtfsWriter

Minimal but spec-keyed writer for SCO HTFS (High Throughput File System). Emits a real `HtfsMagic`-tagged superblock at byte offset 512 (sector 1) followed by an inode array (one block per 4 inodes) and per-file single-extent layout in subsequent blocks.

Scope. S5-derived HTFS uses 512-byte blocks (BlockSize knob can override), block-based inode array immediately after the SB, and directory bodies storing 16-byte name + inode entries. The on-disk magic + s_isize/s_fsize fields are spec-compliant so `TryParse` recognises the image. Real SCO HTFS additionally maintains a journal, a duplicate superblock at sector S, and extent btrees — all out of scope for the WORM writer.

### HtfsExtentMap

Walks an HTFS image written by `HtfsWriter` and yields its on-disk extents: superblock + inode array become `MetadataReserved`; each directory body is metadata too; each file extent is `Used`.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BlockSize` | Enum | `512` | `512`, `1024`, `2048` | Block size in bytes (S5-style HTFS supports 512/1024/2048). |
| `InodeCount` | Integer | `64` | any | Reserved inode slots in the inode array (default 64; cap 256). |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 16 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- SCO OpenServer 5 Development System documentation, sys/fs/htfs/htfs_fs.h — the vendor header defining the on-disk structures (no stable public URL)
- https://en.wikipedia.org/wiki/SCO_OpenServer — Wikipedia overview of the host OS

