# JFS1 (OS/2 original IBM JFS) (`Jfs1`)

IBM JFS1 (OS/2 original) — WORM writer + nested-directory reader.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.jfs1` |
| Recognised extensions | `.jfs1` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4A 46 53 31` | 0 | 0.70 |

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

By moving what is out of place, through `Jfs1BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Jfs1FormatDescriptor

OS/2 original IBM JFS1 format descriptor — distinct from `FileSystem.Jfs` which targets the Linux JFS2 derivative. WORM writer + reader with real nested subdirectories, defrag/purge/conversion, fileset optimizer, and an options schema (BlockSize / AggregateBlockSize / VolumeLabel). References:

Reference: IBM JFS for OS/2 Warp Server documentation (1999-2000), pre-Linux-port. Magic "JFS1" ASCII at byte offset 0 with s_version = 1. The descriptor refuses any image where s_version >= 2 so it cannot steal Linux-JFS detection.

Hierarchy: real — directories nest via writer-emitted dirent chains (4-byte LE inode + 1-byte nlen + name) anchored from inode 2.

### Jfs1Reader

Read-side companion to `Jfs1Writer`. Walks the JFS1 superblock, inode array, and writer-emitted directory blocks to surface every file at its full nested path.

### Jfs1Writer

Minimal but spec-keyed writer for OS/2 JFS1 (the original IBM JFS that shipped with OS/2 Warp Server). Emits a real "JFS1"-magic superblock at offset 0 with `s_version = 1` (distinguishing from Linux JFS2 which uses `s_version >= 2`) followed by an inode table and per-file single-extent data blocks.

Scope. OS/2 JFS1's on-disk format is documented in the IBM JFS for OS/2 Technical Reference. The writer covers: superblock with configurable block + aggregate-block size, inode array (256-byte dinodes), single-block directory bodies with (inode + nlen + name) dirents, single-extent file bodies. The dmap/IAG bitmap chain, secondary AIT/AIM trees, dtree B+ index pages, and the inline data extents larger than one block are out of WORM scope.

### Jfs1ExtentMap

Walks a JFS1 image written by `Jfs1Writer` and yields its on-disk extents for purge + defrag.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `AggregateBlockSize` | Enum | `4096` | `1024`, `2048`, `4096` | Aggregate block size for the dmap chain (usually equals BlockSize). |
| `BlockSize` | Enum | `4096` | `1024`, `2048`, `4096` | JFS1 block size in bytes (IBM OS/2 spec allows 1024/2048/4096). |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 16 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- IBM "JFS for OS/2 Warp Server for e-business" documentation (1999-2000) — the original vendor documentation of the pre-Linux JFS1 (no stable public URL)
- https://jfs.sourceforge.net/ — the open-sourced JFS project, useful for contrasting the later JFS2-derived layout
- https://en.wikipedia.org/wiki/JFS_(file_system) — Wikipedia overview of the JFS family

