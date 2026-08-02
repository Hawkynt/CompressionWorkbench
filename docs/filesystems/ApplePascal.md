# Apple UCSD Pascal (`ApplePascal`)

Apple UCSD Pascal disk volume (Apple II/III/Lisa); 512-byte blocks, contiguous extents, max 77 entries; flat (no subdirs).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.pvol` |
| Recognised extensions | `.pvol`, `.pdv`, `.pas` |

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
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### ApplePascalFormatDescriptor

Descriptor for Apple UCSD Pascal disk volumes (Apple II, Apple III, Lisa Pascal — late 1970s / early 1980s). Volume directory header is at fixed disk block 2 (file offset 0x400); files are stored as contiguous block extents with at most 77 directory entries.

Flat-only by spec. Apple Pascal does not support subdirectories — its 26-byte directory entry has no parent-pointer or nested-volume indirection. Writer / reader treat all inputs as living at the volume root; a leaf-name-only round trip is the maximum possible. This is honest and documented in the writer's xmldoc.

Spec. Apple Pascal Operating System Reference Manual (1980).

References:

### ApplePascalReader

Reads Apple UCSD Pascal disk volumes (Apple II / Apple III / Lisa Pascal, late 1970s–early 1980s). The Apple Pascal filesystem is extent-based — every file is a single contiguous block range, the directory holds at most 77 entries, and the entire volume directory fits in 2 KB (blocks 2..5).

Volume directory header (26 bytes, starts at block 2 = offset 0x400 on a 512-byte-block image; little-endian throughout): 0x00 u16 first block of the directory (=0) 0x02 u16 block after directory (=6) 0x04 u16 entry type (0 = volume header) 0x06 byte volume-name length (1..7) 0x07 char[7] volume name (uppercased ASCII) 0x0E u16 total blocks on volume 0x10 u16 number of files (1..77) 0x12 u16 first block to access (cached) 0x14 u32 last modification date (Pascal packed format) 0x18 byte[4] reserved

File entry layout (26 bytes each, packed back-to-back after the header): 0x00 u16 start block 0x02 u16 end block (exclusive — file occupies [start..end)) 0x04 u16 file kind (0=untyped, 1=xdsk, 2=code, 3=text, 4=info, 5=data, 6=graf, 7=foto) 0x06 byte filename length (1..15) 0x07 char[15] filename (uppercased ASCII) 0x16 u16 bytes used in last block (1..512) 0x18 u32 last modification date (Pascal packed)

### ApplePascalWriter

Writes Apple UCSD Pascal disk volumes (Apple II / Apple III / Lisa Pascal era, late 1970s–early 1980s). UCSD Pascal is an extent-based filesystem: every file occupies a contiguous block range. The volume directory at disk block 2 (file offset 0x400) holds the 26-byte volume header followed by up to 77 file entries.

Flat by spec. Apple Pascal volumes are flat — there are no subdirectories. Files written with '/' or '\' in the input name have those chars stripped to a single 15-char short name. The writer enforces the 77-entry maximum and rejects names that don't fit.

Always 512-byte blocks (spec-mandated); the only sizing knob is the total block count. Typical sizes: 280 blocks (140 KB single-sided 5.25" floppy), 560 (280 KB double-sided), or larger for ProFile / Lisa hard disks. Pascal convention: volume size in blocks is a multiple of 8 (one allocation tile = 8 blocks = 4 KB).

### ApplePascalExtentMap

Walks an Apple Pascal volume and emits the on-disk byte layout: boot blocks (0..1) as metadata-reserved, volume directory (blocks 2..5) as metadata-reserved, each file's contiguous extent as a Used block. Any remaining bytes are implicitly Free.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BlockSize` | Enum | `512` | `512` | Apple Pascal volumes always use 512-byte blocks — fixed by spec. |
| `VolumeName` | String | `PASCAL` | any | Volume name (1..7 ASCII chars, uppercased on disk). |
| `VolumeSize` | Enum | `Auto` | `Auto`, `280`, `560`, `1024`, `1600`, `2048` | Total volume size in 512-byte blocks. Pascal convention: multiples of 8 (8-block allocation tiles). 280 = 140 KB SS floppy, 560 = 280 KB DS floppy. |

## Storage methods

- `stored` — Stored

## Further reading

- Apple Pascal Operating System Reference Manual (Apple Computer, 1980) — the original vendor spec for the UCSD-Pascal volume layout
- https://github.com/fadden/CiderPress2 — CiderPress II, maintained implementation covering Apple Pascal volumes
- https://en.wikipedia.org/wiki/UCSD_Pascal — Wikipedia overview of the UCSD p-System family

