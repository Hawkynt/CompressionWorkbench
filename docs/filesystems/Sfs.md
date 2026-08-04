# Amiga SFS (`Sfs`)

Amiga Smart Filesystem volume — files, and a layout pass over them.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.sfs` |
| Recognised extensions | `.sfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `53 46 53 00` | 0 | 0.95 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `SfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### SfsFormatDescriptor

Read-only descriptor for Amiga Smart Filesystem (SFS) volume images. SFS is the OFS/FFS replacement used by AmigaOS 4 and AROS, with the complete spec at http://www.xs4all.nl/~hjohn/SFS/ (Amiga SFS spec). Surfaces the parsed root block as a structured metadata bundle; per-file enumeration would require walking the object-container B+ tree. References:

The walk to the files is implemented in `SfsVolume` and the volumes are written by `SfsWriter`, both following the block structures in AROS's own SFS source. So the root-block surface above is no longer all there is: files are listed, extracted, written and laid out again.

There is no SFS driver or checker on Linux to hold a volume up against, so what stands in for one is the format's own arithmetic. Every block that carries a header records its own block number and is checksummed by its longwords summing to zero, and a volume that failed either would be rejected by any reader — including this one, which checks both before it believes a block is what it claims.

What is written is the simplest shape the structures allow: one object container for a flat root directory, one leaf of extents, one node container. Hash tables, soft links, sub-directories and multi-level trees are shapes the format has and this does not produce.

### SfsWriter

Builds an Amiga Smart File System volume: root block, bitmap, admin space, object node table, extent tree, root directory and the files themselves.

SFS keeps a file's blocks out of the file's own entry. The directory entry names one key; the key indexes a tree of extents, each of which says how many blocks it covers and which key comes next. So a file is a chain through that tree, and where the chain's links point is the only record of where its bytes are — which is exactly what a layout pass rewrites.

Every block carrying a header is checksummed by the whole block's longwords summing to zero, and every one of them also records its own block number, so a block that moved without being rewritten fails both checks at once.

What this writes is the simplest volume the structures allow: one object container for a flat root directory, one leaf of extents, one node container. Hash tables, soft links, sub-directories and multi-level trees are shapes the format has and this does not produce.

### SfsExtentMap

Describes an SFS volume block by block: what the volume needs to describe itself, what each file owns, and what is free.

The root block, its copy at the far end, the bitmap, the admin space, the object node table, the extent tree and the root directory are all reserved. Each records its own block number and is checksummed over its whole block, so moving one without rewriting it would leave a block that fails both checks — and the volume with it.

### SfsLayout

The structures of the Amiga Smart File System, as the filesystem's own source lays them out.

These track rom/filesys/SFS/FS in AROS, which is John Hendrikx's SFS with the block structures unchanged. Everything is big-endian, and every block that carries a header is checksummed the same way: the header's checksum word is whatever makes the block's longwords sum to zero.

The root block's field offsets were four bytes short here before. It carries two reserved longwords after the flag byte, not one, so everything from the partition's first byte onwards was read one word early — the block count came out of the partition's last byte and the block size out of the block count. Nothing noticed, because nothing read past the root block.

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/aros-development-team/AROS/tree/master/rom/filesys/SFS — AROS SFS implementation — maintained open source
- John Hendrikx's original SFS specification (the xs4all.nl page cited above; now web-archived)
- https://en.wikipedia.org/wiki/Smart_File_System — Wikipedia article

