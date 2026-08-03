# APFS (`Apfs`)

Apple File System container image (full-scope in-place mutation: omap + FS-tree splits, nested paths, tree height growth; structural validator).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.apfs` |
| Recognised extensions | `.apfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4E 58 53 42` | 32 | 0.95 |

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

By moving what is out of place, through `ApfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### ApfsFormatDescriptor

References:

### ApfsReader

Reads Apple File System (APFS) images per Apple's "Apple File System Reference" (public spec). Walks the NXSB → container OMAP → APSB → volume OMAP → filesystem B-tree chain and extracts file data via `FILE_EXTENT` records.

### ApfsWriter

Creates minimal Apple File System (APFS) container images per Apple's "Apple File System Reference" (public spec).

The writer emits real NXSB and APSB superblocks, container and volume object maps, and a populated file-system B-tree containing inode, directory-record and file-extent records. All objects carry valid Fletcher-64 checksums per the spec.

The FS B-tree grows automatically: when the inode / directory-record / file-extent records overflow a single node, they spill into several leaf nodes beneath an internal index node (a 2-level tree), so directories with many entries round-trip correctly. The tree depth is capped at two levels — the internal root holds one separator per leaf, which bounds the volume at a few hundred thousand small files (ample for image creation); a deeper tree is not emitted.

Scope cuts: single container / single volume / single checkpoint / FS B-tree limited to two levels (root + leaves) / no snapshots / no encryption / no clones / no inline compression / no reaper / no spaceman (the allocation file is unused in a read-only writer context — macOS would require it for mount, but fsck_apfs structural validation of the superblocks and B-trees still passes).

### ApfsLayout

Where an APFS container keeps its own structures, and where each file's extent record says its bytes are.

The container's blocks are not all at the front. Every change made in place allocates from the image's tail — new B-tree nodes, new object map entries — so a volume that has been written to since it was made has its trees scattered past the file data. Anything that treats "past the last file" as free space is therefore writing over the map of the volume.

A file's position is one field: phys_block_num in its file extent record, in a leaf of the filesystem tree. Each block carries its own Fletcher-64, so rewriting that field means rewriting one leaf's checksum and nothing else — no tree rebuild, and no growth.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 255 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://developer.apple.com/support/downloads/Apple-File-System-Reference.pdf — Apple File System Reference, the official on-disk format specification
- https://github.com/libyal/libfsapfs — libfsapfs, maintained open-source APFS reader with format documentation
- https://en.wikipedia.org/wiki/Apple_File_System — Wikipedia overview

