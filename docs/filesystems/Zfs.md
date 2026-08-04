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

By moving what is out of place, through `ZfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### ZfsFormatDescriptor

Descriptor for ZFS pool images — four 256 KB vdev labels (NVList + uberblock ring) around the pool data area; WORM pool writer + reader round-trip. References:

How this pool is laid out again by moving.

A block is named by a device address inside a block pointer, and the block pointer carries a Fletcher-4 over what it points at. Moving bytes leaves that check good — the bytes do not change — and breaks every one above it: the pointer sits in an indirect block whose own check sits in the pointer above, up to the uberblock. So the addresses are written as the pass goes and the checks are taken again from the bottom up once it is over.

The space maps are not an obstacle here. This writer sets metaslab_array to zero, so a pool it produces has none, and nothing but the pointers records where a block is.

What made this the longest of the walks is the path itself: the uberblock, the meta object set, a dnode array, the dataset's own object set, another dnode array, and then the file's indirect blocks. Every pointer along it is written down once, which is what `ZfsLayout` is for.

### ZfsReader

Reads a ZFS pool image produced by `ZfsWriter` (and compatible minimal spec-aligned images). Traverses: vdev label → highest-TXG uberblock → MOS objset → object directory ZAP → DSL dataset → dataset objset → master node / ROOT dir ZAP → file dnodes. Validates Fletcher-4 checksums on all traversed blocks.

### ZfsWriter

Writes a minimum-viable WORM ZFS pool image — single-vdev, single-dataset, flat root directory, Fletcher-4 checksums, no compression/encryption/snapshots. Validates round-trip through `ZfsReader`.

Image layout: 0 .. 256 KB L0 vdev label 256K .. 512K L1 vdev label 512K .. (end - 512K) Data area (MOS, DSL, ZAP, file data) end-512K .. end-256K L2 vdev label end-256K .. end L3 vdev label

### ZfsLayout

Walks a pool the way `ZfsReader` does, but writes down where it has been: the byte offset of every block pointer on the path to a file's data, and of every block those pointers name.

A block pointer carries a Fletcher-4 over what it points at, so moving a block leaves its own check good and every check above it stale. Putting that right means knowing the path — which the reader traverses but never records, because reading only ever needs the block in front of it.

The path is long: the uberblock, the meta object set, a dnode array, the dataset's own object set, another dnode array, and then the file's indirect blocks. Each step is written down here once, and the checks are taken again from the bottom up after a layout pass.

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

