# ATR (Atari 8-bit) (`Atari8`)

Atari 8-bit AtariDOS 2.x floppy disk image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.atr` |
| Recognised extensions | `.atr` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `96 02` | 0 | 0.90 |

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

By moving what is out of place, through `Atari8BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Atari8FormatDescriptor

References:

### Atari8Reader

Reader for Atari 8-bit AtariDOS 2.x `.atr` disk images.

ATR header (16 bytes, little-endian):

AtariDOS 2.x uses sectors 1..720 (sector numbers are 1-based). Sectors 361-368 hold the directory (8 sectors x 8 entries x 16 bytes = 64 slots). Each data sector carries 3 bytes of metadata in its last 3 bytes: next-sector-number (10-bit split across two bytes) + byte-count-in-sector.

Scope: AtariDOS 2.0S single-density 128-byte sectors (the dominant case). Higher densities parse but chain-trailer layout is identical.

### Atari8Writer

Builds a fresh Atari 8-bit AtariDOS 2.x `.atr` disk image from scratch (WORM).

ATR layout: 16-byte header (magic 0x0296 + paragraph count + sector size) followed by raw sector data. SS/SD (single-sided / single-density) has 720 sectors of 128 bytes each, totaling 92 176 bytes. Sectors are numbered 1-based.

AtariDOS 2.0S reserves sector 360 for the VTOC and sectors 361-368 for the directory (8 sectors * 8 directory slots of 16 bytes each = 64 files). File data is allocated from sector 4 onward (sectors 1-3 are boot, 360-368 = directory). Each data sector's last 3 bytes store: [file# top-bits, next-sector-hi] [next-sector-lo] [byte-count].

Filenames are 8 chars + 3 chars extension, upper-case ATASCII, space-padded.

### Atari8ExtentMap

Walks an Atari 8-bit ATR image (AtariDOS 2.x) and yields the actual on-disk byte layout — 16-byte ATR header + sector 360 (VTOC) + sectors 361-368 (directory) as metadata, every per-file sector chain as one or more contiguous-run extents (chain followed via the 3-byte trailer), and the un-attributed sectors as Free.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `WriteProtect` | Boolean | `false` | any | Sets the ATR header flags byte (offset 15, bit 0). Emulators that honour the flag (Atari800, Altirra, …) will refuse to write the image. |

## Storage methods

- `stored` — Stored

## Further reading

- https://www.atarimax.com/jindroush.atari.org/afmtatr.html — ATR file format description (Jindroush archive); the header layout defined by Nick Kennedy's SIO2PC
- Atari DOS 2.0S/2.5 Reference Manual (Atari, Inc.) — VTOC + directory sector layout on the SS/SD 720-sector disk
- https://en.wikipedia.org/wiki/Atari_DOS — Wikipedia overview of the Atari 8-bit DOS family

