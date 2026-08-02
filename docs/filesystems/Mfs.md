# MFS (Macintosh File System) (`Mfs`)

Classic Macintosh MFS filesystem image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.mfs` |
| Recognised extensions | `.mfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `D2 D7` | 1024 | 0.80 |

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

By moving what is out of place, through `MfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### MfsFormatDescriptor

R/W descriptor for Classic Macintosh MFS (Macintosh File System) 400 KB floppy volumes — the flat-directory predecessor of HFS, MDB magic 0xD2D7. References:

### MfsWriter

Builds a minimal MFS disk image.

### MfsExtentMap

Walks a Macintosh MFS image (0xD2D7 magic at offset 1024) and yields the actual on-disk byte layout — system area (boot blocks + MDB), file directory area, every file's contiguous data range (per the writer's simplified linear allocation), and the unused tail as Free. MFS uses a packed 12-bit block map but our writer-produced images are linear, so we walk directory entries and emit one extent per file based on (firstBlock, size).

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 27 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- Apple "Inside Macintosh, Volume II" (File Manager chapter, Addison-Wesley 1985) — the canonical MFS description
- https://en.wikipedia.org/wiki/Macintosh_File_System — Wikipedia article

