# Apple DOS 3.3 (`AppleDos`)

Apple II DOS 3.3 floppy disk image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.dsk` |
| Recognised extensions | `.dsk`, `.do` |

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

By moving what is out of place, through `AppleDosBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### AppleDosFormatDescriptor

References:

### AppleDosReader

Reader for Apple DOS 3.3 `.dsk`/`.do` disk images.

Layout: 35 tracks x 16 sectors x 256 bytes = 143 360 bytes. Catalog track is 17. VTOC (track 17, sector 0) points to the first catalog sector. Each catalog sector holds 7 x 35-byte directory entries. Each entry has a track/sector pointer to a "T/S list" sector whose body is an array of (track, sector) pairs pointing at the file's data sectors. A file may span multiple chained T/S list sectors.

### AppleDosWriter

Builds a fresh Apple DOS 3.3 `.dsk` / `.do` disk image (143 360 bytes) from scratch (Write-Once, Read-Many).

Layout: 35 tracks x 16 sectors x 256 bytes. VTOC lives at track 17, sector 0, and points at the first catalog sector (we use track 17, sector 15 and chain backwards toward sector 1). Each catalog sector holds seven 35-byte entries at offset 0x0B. Each file has a T/S list (track 17 sectors are avoided for file data so the catalog stays intact) that points at 122 data-sector pairs per T/S-list sector.

DOS 3.3 filenames are 30 bytes of high-bit-set ASCII padded with 0xA0. We upper-case and truncate at 30 characters.

### AppleDosExtentMap

Walks an Apple DOS 3.3 image (143,360 bytes, 35 tracks × 16 sectors, 256-byte sectors) and yields its actual on-disk byte layout — track 17 VTOC + catalog as metadata, every per-file (T/S list + data) sector chain as contiguous-run extents, and unallocated sectors as Free.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeNumber` | Integer | `254` | any | Disk volume number stored at VTOC offset 0x06. Apple DOS uses this to identify which physical floppy is in the drive. Range 1..254 (default 254). |

## Storage methods

- `stored` — Stored

## Further reading

- "Beneath Apple DOS" (Don Worth & Pieter Lechner, Quality Software, 1981) — the canonical DOS 3.3 on-disk reference (VTOC, catalog, track/sector lists)
- https://github.com/fadden/CiderPress2 — CiderPress II, maintained implementation covering DOS 3.3 disk images
- https://en.wikipedia.org/wiki/Apple_DOS — Wikipedia overview

