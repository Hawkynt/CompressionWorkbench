# CramFS (`CramFs`)

Linux Compressed ROM filesystem

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.cramfs` |
| Recognised extensions | `.cramfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `45 3D CD 28` | 0 | 0.95 |

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

By moving what is out of place, through `CramFsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### CramFsFormatDescriptor

References:

### CramFsReader

Reads a CramFS (Compressed ROM Filesystem) image. CramFS is a Linux read-only compressed filesystem where file data is stored as independently-compressed 4 KB zlib blocks.

### CramFsWriter

Writes a CramFS (Compressed ROM Filesystem) image. Entries are collected via `AddFile`, `AddDirectory`, and `AddSymlink`, and the entire image is serialised on `Dispose`.

## Storage methods

- `cramfs` — CramFS

## Further reading

- https://docs.kernel.org/filesystems/cramfs.html — Linux kernel cramfs documentation
- https://github.com/torvalds/linux/tree/master/fs/cramfs — mainline implementation (its README documents the on-disk layout)
- https://en.wikipedia.org/wiki/Cramfs — Wikipedia overview

