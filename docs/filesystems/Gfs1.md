# GFS (Sistina/Red Hat, original) (`Gfs1`)

Sistina GFS (pre-GFS2) — WORM writer + nested-directory reader.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.gfs` |
| Recognised extensions | `.gfs`, `.gfs1` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `01 16 19 70 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 05 1D` | 65536 | 0.92 |
| `01 16 19 70` | 65600 | 0.65 |

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

By moving what is out of place, through `Gfs1BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Gfs1FormatDescriptor

Sistina/Red Hat GFS (pre-GFS2) format descriptor. WORM writer + reader with real nested subdirectories, defrag/purge/conversion, fileset optimizer, and an options schema (BlockSize / JournalCount / LockProto / LockTable). References:

Reference: Sistina GFS / OpenGFS (the pre-Red Hat patches). Meta-header magic 0x01161970 appears at every metadata block start. Superblock at byte offset 65536. GFS vs GFS2 disambiguated by sb_multihost_format = 1900 (GFS) vs 1901 (GFS2). We anchor the magic at offset 65536 + 0x40 so detection doesn't collide with FileSystem.Gfs2.

Hierarchy: real — directories nest via the writer's inode + (4-byte BE inode + 1-byte nlen + name) dirent chain (single-block dirs cap one BB of entries).

Lock proto / table: GFS1 requires sb_lockproto ("lock_nolock" for standalone, "lock_dlm" for clustered) + sb_locktable. The writer emits these via the options schema; the real distributed-lock protocol negotiation is out of WORM scope.

### Gfs1Reader

Read-side companion to `Gfs1Writer`. Walks the superblock at byte offset 65536, the inode table immediately following it, and every directory body, surfacing files at full nested paths.

### Gfs1Writer

Minimal but spec-keyed writer for Sistina GFS (the pre-GFS2 distributed filesystem). Emits a real `MhMagicConst`-tagged metaheader superblock at byte offset 65536 with the GFS-specific `sb_multihost_format = 1900`, followed by a packed inode + data area in subsequent 4 KB blocks. Directories are stored as 16-byte entry blocks for round-trip simplicity (kernel GFS1 used full ondir entries — out of WORM scope).

Scope. Real GFS1 maintains a journal per cluster node, a distributed lock table (DLM), a resource group bitmap chain, and a hashed-leaf-block directory layout. The WORM writer skips the journal area + lock proto fields beyond the spec-anchored handle in the SB, and emits a non-hashed single-block directory body.

### Gfs1ExtentMap

Walks a Sistina GFS1 image written by `Gfs1Writer` and yields its on-disk extents.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BlockSize` | Enum | `4096` | `4096` | GFS1 block size (always 4096 per Sistina spec). |
| `JournalCount` | Integer | `1` | any | Number of per-node journals to allocate (1 standalone; >1 for clustered). |
| `LockProto` | Enum | `lock_nolock` | `lock_nolock`, `lock_dlm` | Cluster lock protocol. Use lock_nolock for single-node images. |
| `LockTable` | String | `WORM:gfs1` | any | Lock table identifier (format: clustername:fsname). |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 16 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://sourceforge.net/projects/opengfs/ — OpenGFS, the open continuation of Sistina GFS whose headers define the GFS1 on-disk structures
- https://en.wikipedia.org/wiki/Global_File_System_2 — Wikipedia article covering GFS history and its GFS2 successor

