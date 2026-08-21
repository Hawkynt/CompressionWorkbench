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

Reads the files out of an Amstrad CPC DSK image.

This used to enumerate sectors, and name them after where they sat — T00S0_C1 and so on. That is a true description of a DSK container and no description at all of what is on the disk: every file written to it came back as a list of sectors, so a volume that round-tripped its bytes perfectly reported every one of its files as missing. What a CPC reads is the AMSDOS directory, so that is what this reads.

CP/M records a length only as a count of 128-byte records, so a file comes back rounded up to the next record, padded with zeros. That is the format's own granularity, not a loss in the reading of it.

### CpcDskWriter

Writes a Standard CPC DSK image holding an AMSDOS DATA-format filesystem.

The container is the easy half: a disk info header, then each track's info block followed by its sectors. The filesystem inside it is what a CPC actually reads, and it is ordinary CP/M 2.2 — kilobyte allocation blocks numbered from the start of the disk, the directory in the first two of them, and a directory entry for every sixteen kilobytes of every file.

A disk that numbers its blocks any other way still looks like a disk and still lists filenames; it is only when something follows those numbers to the data that the difference shows, which is why this follows the format rather than a convention of its own.

### CpcDskExtentMap

Describes what occupies each stretch of a CPC DSK image: the container's own headers, the AMSDOS directory, each file's blocks, and the blocks nothing has been given.

What the filesystem allocates is a kilobyte block, but what the image stores is a 512-byte sector, and a track of nine sectors is four and a half blocks — so a block's two sectors are not always next to each other: every other one has a 256-byte Track-Info block in the middle of it.

The map is therefore drawn in sectors and the runs coalesced only where the bytes really do run on. Describing a straddling block as one span claimed the first half and left the second unclaimed, and free-space wiping zeroes whatever the map does not claim — so eight of fourteen files came back with holes in them from a verb that is supposed to touch nothing that is in use.

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

