# EFS (SGI Extent File System) (`Efs`)

SGI EFS (pre-XFS IRIX filesystem) — WORM writer + hierarchical reader.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.efs` |
| Recognised extensions | `.efs`, `.efsimg` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `00 07 29 59` | 540 | 0.85 |
| `00 07 29 59` | 24 | 0.60 |

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

By moving what is out of place, through `EfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### EfsFormatDescriptor

SGI EFS (Extent File System) format descriptor — the pre-XFS native filesystem used on IRIX before 5.3 (1994). Surfaces a real WORM writer that emits a spec-keyed superblock + single-cylinder-group inode table + per-file single-extent layout, plus defrag/purge/conversion/optimizer wiring. References:

Reference: Linux kernel fs/efs/efs_fs_sb.h, IRIX sys/fs/efs_fs.h. Superblock at offset 0 (sector 0, 512-byte sectors). Magic 0x00072959 (big-endian u32) at byte offset 0x1C inside the superblock (fs_magic).

Hierarchy: real — directories nest via the writer's directory inode chain (single-block directories; bodies use inode + nlen + name dirents). Reader recurses from inode 2 (root) and surfaces each entry at its full path.

### EfsReader

Read-side companion to `EfsWriter`. Walks the on-disk superblock + inode table + directory blocks emitted by our writer and yields the file tree as a flat list of `EfsEntry`.

### EfsWriter

Minimal but spec-aware writer for an SGI EFS (Extent File System) image. Produces a real `EfsMagic`-tagged superblock at sector 0, a packed inode table directly after the superblock, and the directory + file data in subsequent 512-byte basic blocks.

Scope. The on-disk layout follows the IRIX efs_fs.h header field positions (size in BB, first_cg, ncg, cg_isize, magic) so existing `TryParse` recognises the image. File bodies are stored as a single direct extent each; directory entries use the variable-length efs_dent format (inode + nlen + name) but always land in a single directory block, so per-directory total payload is capped at one BB minus the dir header. This matches the "flat-+-nested" subset reiserfsprogs would generate for a freshly-created small disk.

### EfsExtentMap

Walks an EFS image written by `EfsWriter` and emits an extent map: superblock + inode table become `MetadataReserved`; each directory body and each file's data extent becomes `Used`; any unallocated tail bytes are surfaced as `Free`. Drives `UnusedSpaceWiper` for the purge capability.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BlockSize` | Enum | `512` | `512` | EFS basic-block size in bytes (always 512 per IRIX spec). |
| `CylinderGroupSize` | Integer | `32` | any | Cylinder group size in 512-byte basic blocks. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 6 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/torvalds/linux/tree/master/fs/efs — Linux kernel EFS driver (read-only), the maintained on-disk reference
- IRIX sys/fs/efs_fs.h — the original SGI header defining the superblock and extent layout
- https://en.wikipedia.org/wiki/Extent_File_System — Wikipedia overview

