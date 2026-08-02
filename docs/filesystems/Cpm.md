# CP/M 2.2 (8" SSSD) (`Cpm`)

CP/M 2.2 disk image (8" SSSD canonical geometry) — 77 tracks × 26 sectors × 128 B, 1024-byte allocation blocks, 64-entry directory, 8.3 filenames.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.cpm` |
| Recognised extensions | `.cpm`, `.dsk` |

## Detection

No byte signature: this format is recognised by its extension and by the
reader accepting the volume's own structures.

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

By moving what is out of place, through `CpmBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### CpmFormatDescriptor

Read+write descriptor for CP/M 2.2 disk images using the 8" SSSD reference geometry (256 256 bytes, 2 reserved tracks, 1024-byte blocks, 64 directory entries). Kaypro/Osborne/Amstrad and other manufacturer-specific geometries are not emitted by the writer; the reader still parses any image that matches this layout. References:

### CpmReader

Reader for CP/M 2.2 disk images (8" SSSD reference geometry). Each file is reconstructed from its directory extents; extents are matched by `(userCode, name.ext)` and ordered by the extent counter before their block lists are concatenated.

### CpmWriter

Writer for CP/M 2.2 disk images using the 8" SSSD reference geometry. Files are split into 16 KB extents; each extent carries up to 16 block numbers and the record count of its final used sector. The writer enforces the built-in disk size limit (241 data blocks, 64 directory entries) and rejects overflow explicitly rather than producing a truncated volume.

### CpmExtentMap

Walks a Digital Research CP/M 2.2 reference disk image (8" SSSD geometry — 256 256 bytes, 2 reserved tracks, 1024-byte allocation blocks, 64-entry directory) and yields the actual on-disk byte layout — the reserved tracks (BIOS) + 2 KB directory blocks as `MetadataReserved`, every per-file allocation-block list as one or more contiguous-run extents, and unused blocks as `Free`. Used by the defrag window's block-map preview.

### CpmLayout

Geometry of the canonical Digital Research CP/M 2.2 reference disk (8" SSSD IBM diskette): 77 tracks × 26 sectors × 128 bytes = 256 256 bytes, 2 reserved tracks for the CP/M BIOS, 1024-byte allocation blocks, 64-entry directory. Not every CP/M variant uses these numbers — implementations like Kaypro, Osborne, and Amstrad had their own DPBs — but 8" SSSD is the format every CP/M-80 BDOS shipped with and is the most widely understood layout.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `UserCode` | Integer | `0` | any | User-area number stored in each CP/M directory entry (byte 0). Range 0..15. CP/M 2.2 uses 0 by default; the BDOS hides entries that don't match the current USER command. |

## Storage methods

- `stored` — Stored

## Further reading

- "CP/M 2.2 Operating System Manual" (Digital Research, 1979) — the original vendor documentation of the directory/extent model
- http://www.moria.de/~michael/cpmtools/ — cpmtools (Michael Haardt), maintained implementation with the diskdefs geometry database
- https://en.wikipedia.org/wiki/CP/M — Wikipedia overview

