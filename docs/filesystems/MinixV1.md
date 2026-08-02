# Minix V1 FS (`MinixV1`)

Minix v1 filesystem image (1987) — read-only.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.minix1` |
| Recognised extensions | `.minix1` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `7F 13` | 1040 | 0.85 |
| `8F 13` | 1040 | 0.85 |

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

By moving what is out of place, through `MinixV1BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### MinixV1FormatDescriptor

Read-only descriptor for the original Minix v1 filesystem (1987, Tanenbaum). 1024-byte blocks, 16-bit zone numbers, 32-byte inodes (7 direct + 1 indirect + 1 double-indirect), magic 0x137F (14-byte names) or 0x138F (30-byte names — Coherent variant). Predecessor to Linux's ext filesystem family. References:

### MinixV1Reader

Reads original Minix v1 filesystem images (1987, Tanenbaum's "Operating Systems: Design and Implementation"). The v1 layout uses 16-bit zone numbers, 16-bit inode counts, 32-byte inodes (7 direct + 1 indirect + 1 double-indirect zone pointer), and 1024-byte blocks. Two magic flavors: 0x137F (14-byte directory names) and 0x138F (30-byte directory names — Coherent / Minix patched variant).

### MinixV1Writer

Builds minimal but spec-correct original Minix v1 filesystem images (1987, Tanenbaum). The v1 on-disk format uses 1024-byte blocks, 16-bit zone numbers, 16-bit inode counts and 32-byte inodes addressing data through 7 direct zones, 1 single-indirect and 1 double-indirect zone. Directory names are 14 bytes (magic `0x137F`) or 30 bytes (magic `0x138F`). Every path component becomes a real directory inode carrying its own `"."`/`".."` entries, so a file added as `"a/b/c.txt"` is stored under nested directories `a` and `a/b`. Files larger than the 7 direct zones (7168 bytes) spill into the single-indirect zone, which addresses a further 512 zones (524 288 bytes); beyond that the double-indirect zone extends the reach further still.

### MinixV1ExtentMap

Reports where a Minix V1 volume's bytes are: its structures, each file's zones under its name, and what nothing holds.

The volume had no layout to report at all, which left every layout-aware verb with nothing to work from — a wipe could not tell live bytes from a removed file's leftovers, and a defragmentation had to read every file out and write a fresh volume to move anything.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `NameLength` | Enum | `14` | `14`, `30` | Directory-entry name width: 14 bytes (magic 0x137F) or 30 bytes (magic 0x138F). |

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/torvalds/linux/blob/master/include/uapi/linux/minix_fs.h — canonical on-disk structures (v1 layout + 0x137F/0x138F magics)
- Tanenbaum & Woodhull, "Operating Systems: Design and Implementation" — the original Minix FS design
- https://en.wikipedia.org/wiki/Minix_file_system — Wikipedia article

