# ROMFS (`RomFs`)

Linux ROM filesystem image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.romfs` |
| Recognised extensions | `.romfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `2D 72 6F 6D 31 66 73 2D` | 0 | 0.95 |

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
| move blocks | yes | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `RomFsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### RomFsFormatDescriptor

R/W descriptor for Linux ROMFS images — the "-rom1fs-" packed read-only filesystem used for boot/initrd media. References:

### RomFsReader

Reads Linux ROMFS filesystem images (romfs v1). Magic: "-rom1fs-" at offset 0. All multi-byte integers are big-endian.

### RomFsWriter

Builds a Linux ROMFS filesystem image from a set of files. Produces a valid romfs v1 image with "-rom1fs-" magic.

### RomFsExtentMap

Walks a Linux ROMFS (romfs v1) image and yields its actual on-disk byte layout. ROMFS is a packed, read-only image: the superblock is followed by a chain of file records, each consisting of a 16-byte header, a null-terminated 16-byte-aligned name, and (for regular files) the data padded to a 16-byte boundary.

Every header+name region — including the "." and ".." records that thread a directory's child chain — is emitted as `MetadataReserved` so a free-space wiper never mistakes live metadata for a gap. File data is emitted as `Used`. The 16-byte alignment padding after each file's data and any trailing slack are left uncovered, so the caller treats them as `Free`.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 16 chars). |

## Storage methods

- `romfs` — ROMFS

## Further reading

- https://www.kernel.org/doc/html/latest/filesystems/romfs.html — kernel documentation — includes the complete on-disk layout
- https://github.com/torvalds/linux/tree/master/fs/romfs — Linux reference implementation
- genromfs — the canonical image-builder tool

