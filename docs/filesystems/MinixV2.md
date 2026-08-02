# Minix V2 FS (`MinixV2`)

Minix v2 filesystem image (1991) — read-only.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.minix2` |
| Recognised extensions | `.minix2` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `68 24` | 1040 | 0.85 |
| `78 24` | 1040 | 0.85 |

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

By moving what is out of place, through `MinixV2BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### MinixV2FormatDescriptor

Read-only descriptor for Minix v2 filesystem (1991). v2 extended the original layout with 64-byte inodes, 32-bit zone numbers, and triple-indirect blocks for large-file support. Magic 0x2468 (14-byte names) or 0x2478 (30-byte names — extended variant). References:

### MinixV2Reader

Reads Minix v2 filesystem images (1991). v2 extended the original Minix layout to support large files: 64-byte inodes (replacing v1's 32-byte), 32-bit zone numbers, and a triple-indirect block. The superblock layout is the same as v1 (1024-byte blocks). Magic 0x2468 (14-byte names) or 0x2478 (30-byte names).

### MinixV2Writer

Builds minimal but spec-correct Minix v2 filesystem images (1991). v2 keeps the v1 superblock and 1024-byte blocks but widens inodes to 64 bytes, zone numbers to 32 bits, and adds a triple-indirect zone for large files. Directory names are 14 bytes (magic `0x2468`) or 30 bytes (magic `0x2478`). Every path component becomes a real directory inode with its own `"."`/`".."` entries, so a file added as `"a/b/c.txt"` is stored under nested directories `a` and `a/b`. Files larger than the 7 direct zones (7168 bytes) spill into the single-indirect zone (256 further zones), then the double-indirect zone, then the triple-indirect zone.

### MinixV2ExtentMap

Reports where a Minix V1 volume's bytes are: its structures, each file's zones under its name, and what nothing holds.

The volume had no layout to report at all, which left every layout-aware verb with nothing to work from — a wipe could not tell live bytes from a removed file's leftovers, and a defragmentation had to read every file out and write a fresh volume to move anything.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `NameLength` | Enum | `14` | `14`, `30` | Directory-entry name width: 14 bytes (magic 0x2468) or 30 bytes (magic 0x2478). |

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/torvalds/linux/blob/master/include/uapi/linux/minix_fs.h — canonical on-disk structures (v2 layout + 0x2468/0x2478 magics)
- https://github.com/torvalds/linux/tree/master/fs/minix — Linux reference implementation
- https://en.wikipedia.org/wiki/Minix_file_system — Wikipedia article

