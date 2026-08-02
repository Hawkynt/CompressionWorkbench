# YAFFS2 (`Yaffs2`)

Yet Another Flash File System v2 (raw NAND image) — read/write with mkyaffs2image-compatible layout.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.yaffs2` |
| Recognised extensions | `.yaffs2`, `.yaffs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `03 00 00 00 01 00 00 00 00 00 00 00` | 0 | 0.50 |

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

By moving what is out of place, through `Yaffs2BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Yaffs2FormatDescriptor

R/W descriptor for YAFFS2 raw-NAND images. Auto-detects chunk/spare layout, surfaces an object table and reconstructed file tree.

Modify semantics — true in-place, log-structured. YAFFS2 is a log-structured flash filesystem by spec: modifying a file means appending fresh chunks at the next free position with a higher seqNumber, never rewriting an existing chunk on the medium. `Add` and `Remove` route through `Yaffs2InPlaceModifier`, which appends at `Length` and never touches bytes in [0, oldLength). The scanner resolves the live view by keeping the chunk with the highest seqNumber per (objectId, chunkId), and treats a header with parent_obj_id == 0xFFFFFFFE as a tombstone.

Supports: list, extract, create, in-place modify, defragment, extent map. References:

### Yaffs2Writer

Builds a YAFFS2 raw-NAND image from scratch, compatible with mkyaffs2image. Layout: 2048-byte chunks + 64-byte packed_tags2 spare areas. Object headers for root dir, file inodes, then data chunks. All data stored uncompressed (YAFFS2 is a flash filesystem, not a compressor).

## Storage methods

- `stored` — Stored

## Further reading

- https://yaffs.net/ — project home — hosts "How YAFFS Works" and the spec documents
- Charles Manning, "How YAFFS Works" (yaffs.net documentation)
- https://en.wikipedia.org/wiki/YAFFS — Wikipedia article

