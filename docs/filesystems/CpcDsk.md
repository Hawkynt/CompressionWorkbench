# CPC DSK (`CpcDsk`)

Amstrad CPC disk image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.dsk` |
| Recognised extensions | `.dsk` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4D 56 20 2D 20 43 50 43` | 0 | 0.95 |
| `45 58 54 45 4E 44 45 44` | 0 | 0.90 |

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

By moving what is out of place, through `CpcDskBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### CpcDskFormatDescriptor

References:

### CpcDskReader

Reads Standard ("MV - CPC") and Extended ("EXTENDED") CPC DSK disk image files. Exposes every sector as a `CpcDskEntry` for raw sector-level access.

### CpcDskWriter

Creates Standard CPC DSK disk images. Files are stored sequentially across sectors starting from track 1; track 0 holds a minimal CP/M-style directory.

### CpcDskExtentMap

Walks an Amstrad CPC DSK disk image (Standard or Extended) and yields the actual on-disk byte layout — the 256-byte Disk Info header + every 256-byte per-track Track Info Block as `MetadataReserved`; the AMSDOS directory area on track 0 side 0 (sectors hosting the directory entries) as `MetadataReserved`; every AMSDOS file's allocated sector list — coalesced to contiguous runs — as `Used`; unallocated sectors as `Free`.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Sides` | Enum | `1` | `1`, `2` | Number of magnetic surfaces. 1 = single-sided (CPC default); 2 = double-sided (PCW / DSDD). |
| `Tracks` | Enum | `40` | `40`, `80` | Number of cylinders per side. 40 = standard CPC 3" / PCW 720 KB side; 80 = double-stepped 3.5" floppy. |

## Storage methods

- `cpcdsk` — CPC DSK

## Further reading

- https://www.cpcwiki.eu/index.php/Format:DSK_disk_image_file_format — CPCWiki's DSK / Extended DSK image format specification
- https://www.seasip.info/Unix/LibDsk/ — John Elliott's LibDsk, the maintained multi-format floppy-image library incl. CPC DSK
- Amstrad AMSDOS documentation (SOFT 968 firmware guide era) — the filesystem stored inside the image

