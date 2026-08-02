# ADF (`Adf`)

Amiga Disk File

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.adf` |
| Recognised extensions | `.adf` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `44 4F 53 00` | 0 | 0.60 |

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

By moving what is out of place, through `AdfBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### AdfFormatDescriptor

References:

### AdfReader

Reads and extracts files from an Amiga Disk File (.adf) image. Supports both OFS (Original File System) and FFS (Fast File System) disk images. Standard DD ADF images are exactly 901,120 bytes (1760 sectors of 512 bytes).

### AdfWriter

Creates Amiga Disk File (.adf) images using the Fast File System (FFS). Produces standard DD disk images of exactly 901,120 bytes (1760 sectors of 512 bytes).

### AdfExtentMap

Walks an Amiga ADF image (901,120 bytes, 1760 × 512-byte sectors) and yields the actual on-disk byte layout — root block + bitmap + boot blocks as metadata, every file's header / extension / data blocks as contiguous-run extents (per-file), and unallocated sectors as Free. Supports both OFS and FFS layouts.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `FileSystemType` | Enum | `FFS` | `FFS`, `OFS` | AmigaDOS boot-block file-system tag: FFS (Fast File System, AmigaOS 2.0+) or OFS (Original File System, Kickstart 1.x). Stored at boot-block offset 3. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 30 chars). |

## Storage methods

- `adf` — ADF

## Further reading

- http://lclevy.free.fr/adflib/adf_info.html — Laurent Clévy's ADF / AmigaDOS (OFS/FFS) on-disk format reference, the de-facto ADF spec
- ADFlib — the reference open-source ADF implementation built on that document
- https://en.wikipedia.org/wiki/Amiga_Disk_File — Wikipedia overview

