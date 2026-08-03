# HFS (Classic) (`Hfs`)

Classic Macintosh HFS filesystem image (pre-HFS+)

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.hfs` |
| Recognised extensions | `.hfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `42 44` | 1024 | 0.80 |

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

By moving what is out of place, through `HfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### HfsFormatDescriptor

References:

### HfsWriter

Builds a spec-compliant Classic HFS disk image per Inside Macintosh: Files (1992), chapter 2 "File Manager".

Layout matches what hfsutils' libhfs expects: 512-byte B*-tree nodes (libhfs hardcodes HFS_BLOCKSZ=512 and validates header-record offsets at exactly 0x00e/0x078/0x0f8/0x1f8). When records can't fit a single leaf, the catalog grows into multiple leaf nodes (chained via the node-descriptor fLink/bLink) and one or more index levels are stacked above them, increasing the tree depth until a single root node fans out over the whole level below it. The header node's BTMapRec caps the catalog at 2048 nodes (no chained map nodes), which still permits well over a thousand entries in a single directory.

Names passed to `AddFile` may contain '/' separators denoting subdirectories. Each path component below the final one becomes a real catalog folder (directory record type 1 + directory thread type 3) with its own dirID, inserted under its parent's dirID; the file lands keyed under its immediate parent folder's dirID.

Current scope cuts:

### HfsExtentMap

Walks a classic HFS image and yields the actual on-disk byte layout — the boot blocks (sectors 0-1) + MDB (sector 2) + alternate MDB + volume bitmap + catalog file as `MetadataReserved`, every file record's data-fork extent as `Used`. The reader only walks the first leaf chain via fLink so coverage matches what the reader can extract.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 27 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- "Inside Macintosh: Files" (Apple Computer, 1992), chapter "Data Organization on Volumes" — the canonical HFS on-disk specification (MDB, catalog/extents B*-trees)
- https://www.mars.org/home/rob/proj/hfs/ — hfsutils (Robert Leslie), the classic open-source HFS implementation
- https://en.wikipedia.org/wiki/Hierarchical_File_System — Wikipedia overview

