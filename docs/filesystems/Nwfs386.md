# NWFS386 (Novell NetWare 386 raw) (`Nwfs386`)

Novell NetWare 386 raw partition — opaque single-entry surface.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.nwfs386` |
| Recognised extensions | `.nwfs386`, `.nw386` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4E 65 74 57` | 0 | 0.60 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | no | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | no | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

It does not.

## How a volume is laid out

### Nwfs386FormatDescriptor

Read-only descriptor for Novell NetWare 386 (NWFS386) raw partition dumps, detected via the "NetW" ASCII prefix at offset 0. DOS partition type `0x65`. The on-disk format is FAT-like but proprietary; no parser is attempted — the image is surfaced as a single opaque entry with metadata.ini noting the partition-type hint. References:

Distinct from FileSystem.Nwfs, which detects via the "HOTFIX00" magic at byte offset 0x4000 (sector-32-aligned NetWare HOTFIX header). NWFS386 here covers raw NWFS partition dumps that expose the "NetW" four-byte tag at offset 0 instead of the HOTFIX area.

## Storage methods

- `stored` — Stored

## Further reading

- https://www.win.tue.nl/~aeb/partitions/partition_types-1.html — partition-type catalogue (0x65 = Novell NetWare)
- https://en.wikipedia.org/wiki/NetWare_File_System — Wikipedia article
- Novell NetWare 386 internal documentation — the on-disk format was never published

