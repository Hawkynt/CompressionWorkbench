# ISO 9660 (`Iso`)

ISO 9660 optical disc image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.iso` |
| Recognised extensions | `.iso` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `43 44 30 30 31` | 32769 | 0.95 |
| `43 44 30 30 31` | 34817 | 0.90 |
| `43 44 30 30 31` | 36865 | 0.85 |

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

By moving what is out of place, through `IsoBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### IsoFormatDescriptor

Format descriptor for ISO 9660 optical disc images. References:

### IsoReader

Reads ISO 9660 (ECMA-119) disc images with optional Joliet and Rock Ridge support.

### IsoWriter

Builds a minimal ISO 9660 (ECMA-119) disc image, optionally with a Joliet Supplementary Volume Descriptor. File names passed to `AddFile` may contain '/' separators; each separated segment becomes a real directory in the on-disc directory-record tree (with its own extent, "." and ".." records, and a matching path-table entry) rather than being flattened into the root directory.

When `EnableJoliet` is set (the default), the writer emits a second, parallel directory tree carrying the original long, mixed-case, Unicode names as UCS-2 (UTF-16) big-endian, described by a Supplementary Volume Descriptor (type 2) with the UCS-2 level-3 escape sequence and its own L/M path tables. Both trees reference the same shared file-data extents — only the directory/name metadata differs: the primary tree carries short ECMA-119 (uppercase 8.3-ish, ";1") names, the Joliet tree the real long names.

### IsoExtentMap

Walks an ISO 9660 image and yields its actual on-disk byte layout — the 32 KiB system area, every Volume Descriptor sector (PVD/SVD/VDST), the path tables, every directory record's contiguous extent (ISO 9660 spec requires single-extent files), and the trailing free space. Each file surfaces as exactly one Used extent because ECMA-119 mandates contiguous allocation.

Streaming: reads only the volume descriptor sectors + one directory's contents at a time through a `SectorCache`. A 100 GB DVD/BD image needs only ~256 MB of cache regardless of size — directory bytes are pulled on-demand from disk rather than loaded whole.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Application` | String | `` | any | ECMA-119 Application Identifier. Max 128 a-characters. |
| `Joliet` | Boolean | `true` | any | Emit a Joliet Supplementary Volume Descriptor with a parallel UCS-2 directory tree preserving long/mixed-case filenames. Disable for strict ECMA-119 8.3 uppercase only. |
| `Publisher` | String | `` | any | ECMA-119 Publisher Identifier. Max 128 a-characters. |
| `SystemId` | String | `` | any | ECMA-119 System Identifier. Max 32 a-characters. |
| `VolumeLabel` | String | `CDROM` | any | ECMA-119 Volume Identifier shown by file managers as the disc label. Max 32 d-characters (A-Z, 0-9, _). |

## Storage methods

- `stored` — Stored

## Further reading

- https://ecma-international.org/publications-and-standards/standards/ecma-119/ — ECMA-119 (the freely available equivalent of ISO 9660), the defining standard
- https://github.com/torvalds/linux/tree/master/fs/isofs — Linux kernel implementation
- https://en.wikipedia.org/wiki/ISO_9660 — Wikipedia overview (incl. Joliet / Rock Ridge extensions)

