# ZFS (`Zfs`)

ZFS pool image — single-vdev, single-dataset, flat root directory (WORM writer). Fletcher-4 checksums, NV_BIG_ENDIAN XDR label, pool version 28.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.zfs` |
| Recognised extensions | `.zfs`, `.zpool` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `0C B1 BA 00 00 00 00 00` | 131072 | 0.90 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | yes | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### ZfsFormatDescriptor

Descriptor for ZFS pool images — four 256 KB vdev labels (NVList + uberblock ring) around the pool data area; WORM pool writer + reader round-trip. References:

Why this pool is laid out again by rebuilding rather than by moving, and what is actually in the way.

A block is named by a device address inside a block pointer, and the block pointer carries a Fletcher-4 over what it points at. Moving bytes leaves that check good — the bytes do not change — but breaks every one above it: the pointer sits in an indirect block whose own check sits in the pointer above, up to the uberblock. That is the same shape HAMMER2 turned out to have, and HAMMER2 moves in place.

The space maps are not the obstacle they were first written down as. This writer sets metaslab_array to zero, so a pool it produces has none, and nothing but the pointers records where a block is.

What is left is the length of the chain. Reaching a file's data means the uberblock, the meta object set, a dnode array, the dataset's own object set, another dnode array, and then the file's indirect blocks — and a mover has to know the byte offset of every pointer along that path, which nothing here records today. It is the largest of the walks, not a different kind of problem.

### ZfsReader

Reads a ZFS pool image produced by `ZfsWriter` (and compatible minimal spec-aligned images). Traverses: vdev label → highest-TXG uberblock → MOS objset → object directory ZAP → DSL dataset → dataset objset → master node / ROOT dir ZAP → file dnodes. Validates Fletcher-4 checksums on all traversed blocks.

### ZfsWriter

Writes a minimum-viable WORM ZFS pool image — single-vdev, single-dataset, flat root directory, Fletcher-4 checksums, no compression/encryption/snapshots. Validates round-trip through `ZfsReader`.

Image layout: 0 .. 256 KB L0 vdev label 256K .. 512K L1 vdev label 512K .. (end - 512K) Data area (MOS, DSL, ZAP, file data) end-512K .. end-256K L2 vdev label end-256K .. end L3 vdev label

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `64 MB`, `128 MB`, `256 MB` | Total pool image size (at least 64 MB). |
| `VolumeLabel` | String | `compworkbench` | any | ZFS pool name stored in the vdev-label NVList. |

## Storage methods

- `stored` — Stored

## Further reading

- Sun Microsystems, "ZFS On-Disk Specification" (2006 draft) — vdev labels, uberblocks, DMU structures
- https://github.com/openzfs/zfs — OpenZFS — the maintained implementation
- https://openzfs.github.io/openzfs-docs/ — OpenZFS documentation
- https://en.wikipedia.org/wiki/ZFS — Wikipedia article

