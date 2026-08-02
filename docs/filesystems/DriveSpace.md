# DriveSpace CVF (`DriveSpace`)

Microsoft DriveSpace compressed volume file MS-DOS 6.22+/Windows 95 (MDBPB/MDFAT/BitFAT layout; stored runs, VFAT LFN)

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.cvf` |
| Recognised extensions | `.cvf` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4D 53 44 53 50 36 2E 32` | 3 | 0.85 |
| `44 52 56 53 50 41 43 45` | 0 | 0.80 |

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

By moving what is out of place, through `DoubleSpaceBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### DriveSpaceFormatDescriptor

References:

### DoubleSpaceReader

Reads Microsoft DoubleSpace / DriveSpace Compressed Volume Files (CVF).

The MDBPB (offset 0) starts with a standard FAT BPB (first 36 bytes) and is followed by CVF-specific fields at offset 36 (CvfSignature, CvfVersion, MdfatStart/Len, BitFatStart/Len, DataStart/Len). The reader follows the MDFAT indirection when available and falls back to the inline inner data region otherwise.

### DoubleSpaceWriter

Builds a spec-compliant Microsoft DoubleSpace / DriveSpace Compressed Volume File (CVF).

Layout produced (in sector units, 512 B / sector):

Codec selection: driven by `Variant`:

The 2-byte CVF run header (bit 15 = compressed, low 12 bits = size−1) is shared across all codecs, so the on-disk MDBPB + MDFAT + BitFAT layout is byte-compatible across the family — only the OEM bytes, CvfSignature and inner payload encoding change.

### DoubleSpaceExtentMap

Walks a DoubleSpace/DriveSpace CVF image and yields the actual on-disk byte layout: metadata regions (MDBPB, inner FAT, root dir, MDFAT, BitFAT), every compressed/stored cluster run per file (mapped through MDFAT), and free physical sectors in the DATA region.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Method` | Enum | `ds-lz77` | `stored`, `ds-lz77`, `ds-lz77+`, `ds-lz77++` | Per-cluster codec: stored (no compression) or DS LZ77 at rising effort (+ lazy, ++ iterated). |

## Storage methods

- `stored` — Stored (no compression)
- `ds-lz77` — DS LZ77
- `ds-lz77+` — DS LZ77 (lazy matching, slower better ratio)
- `ds-lz77++` — DS LZ77 (Zopfli-style iteration, best ratio)

## Further reading

- https://github.com/sandsmark/dmsdos — dmsdos, the GPL Linux CVF driver whose source + doc/dmsdos.doc are the de-facto on-disk specification (incl. the JM-0-0 cluster codec)
- Microsoft MS-DOS 6.22 documentation (DriveSpace chapter) — original vendor description
- https://en.wikipedia.org/wiki/DriveSpace — Wikipedia overview

