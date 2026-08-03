# HFS+ (`HfsPlus`)

Apple HFS+ filesystem image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.dmg` |
| Recognised extensions | `.dmg`, `.hfsx`, `.hfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `48 2B` | 1024 | 0.85 |

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

By moving what is out of place, through `HfsPlusBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### HfsPlusFormatDescriptor

References:

### HfsPlusReader

Reads and extracts files from an HFS+ filesystem image. Supports both HFS+ (signature "H+") and HFSX (signature "HX") volumes. The volume header resides at byte offset 1024 within the image.

### HfsPlusWriter

Creates minimal HFS+ filesystem images per Apple TN1150 ("HFS Plus Volume Format").

Produces a 4 MB image with 4 KB block size by default. Files are stored uncompressed in the data fork using single-extent allocation. The catalog file record is the full 248-byte HFSPlusCatalogFile layout with the data fork HFSPlusForkData struct at offset 88 and the resource fork HFSPlusForkData at offset 168, matching TN1150.

### HfsPlusExtentMap

Walks an HFS+ (or HFSX) image and yields the actual on-disk byte layout — the reserved boot region (first 1024 bytes) + volume header + allocation file + catalog file as `MetadataReserved`, every file record's first data-fork extent (HFSPlusForkData.extents[0]) as `Used`. Mirrors what `HfsPlusReader` can extract — leaf chain via fLink, single primary extent per file.

Streaming: never loads the whole image. All reads flow through a `SectorCache` so multi-TB HFS+ images (the catalog file alone can be tens of MB on large volumes) work without OOM.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BlockSize` | Enum | `Auto` | `Auto`, `4 KB`, `8 KB`, `16 KB`, `32 KB`, `64 KB` | HFS+ allocation block size (power of two, 4 KB … 64 KB). Auto picks the size that minimises slack + allocation-bitmap and B-tree overhead. |
| `CaseSensitive` | Boolean | `false` | any | Make filename comparison case-sensitive (emit the HFSX 'HX' signature + binary comparator). |
| `Journal` | Boolean | `true` | any | Enable the volume journal. |
| `JournalSize` | Integer | `8388608` | `8388608`, `16777216`, `33554432`, `67108864` | Journal size in bytes (8/16/32/64 MiB). |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 11 chars). |

## Storage methods

- `hfsplus` — HFS+

## Further reading

- https://developer.apple.com/library/archive/technotes/tn/tn1150.html — Apple Technical Note TN1150 "HFS Plus Volume Format", the canonical spec (incl. HFSX and the journal)
- https://github.com/torvalds/linux/tree/master/fs/hfsplus — Linux kernel implementation
- https://en.wikipedia.org/wiki/HFS_Plus — Wikipedia overview

