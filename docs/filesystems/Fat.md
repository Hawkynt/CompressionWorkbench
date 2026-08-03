# FAT Filesystem Image (`Fat`)

FAT12/FAT16/FAT32 filesystem image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.img` |
| Recognised extensions | `.img`, `.ima`, `.flp`, `.fat` |

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

By moving what is out of place, through `FatBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | yes | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### FatFormatDescriptor

References:

### FatReader

Reads FAT12/FAT16/FAT32 filesystem images. Enumerates files and directories, supports extraction. Handles boot sector parsing, FAT chain following, and directory entry reading with LFN (Long File Name) support.

### FatWriter

Builds FAT12 / FAT16 / FAT32 filesystem images from scratch per the Microsoft FAT specification (FATGEN103, EFI FAT32). Auto-selects FAT type based on cluster count. Emits VFAT / LFN (Long File Name) directory entries transparently when the input filename does not fit in 8.3 (mixed-case, non-ASCII, longer than 8 + 3 chars, or with multiple dots) — DOS-era readers see only the short name, modern readers see the long one.

FAT32 layout: 32 reserved sectors (boot @0, FSInfo @1, backup boot @6), two FAT copies, root directory at cluster 2 with FAT entry = end-of-chain. LFN format: 32-byte slots with attribute 0x0F immediately preceding the matching 8.3 dirent, written in reverse order so the highest-sequence slot is read first; each holds 13 UTF-16LE code units (5+6+2 split) and a checksum of the associated short name.

### FatExtentMap

Walks a FAT12/16/32 image and yields the actual on-disk byte layout — reserved region (boot + FATs + root dir on FAT12/16), every cluster-chain segment per file, and free clusters. Used by the defrag window to render the real fragmented layout before defragmentation runs.

Streaming: only the BPB + the first FAT copy + (FAT12/16) fixed root dir are kept in memory; subdirectory clusters are read from disk one at a time. A 100 GB FAT32 image needs roughly 100 MB of RAM (the FAT itself) rather than 100 GB.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ClusterSize` | Enum | `Auto` | `Auto`, `512 B`, `1 KB`, `2 KB`, `4 KB`, `8 KB`, `16 KB`, `32 KB`, `64 KB` | Allocation unit size. Auto picks the best fit for the image size and FAT type. |
| `FatPlus` | Boolean | `false` | any | FAT+: stores sub-second creation-time precision in DIR_CrtTimeTenth (10 ms granularity instead of 2-second rounding). |
| `FatType` | Enum | `Auto` | `Auto`, `FAT12`, `FAT16`, `FAT32` | Auto selects FAT12/16/32 by cluster count. Force a type when the target system requires it (e.g. FAT32 on a floppy-sized image for a game console). |
| `ForceLongFilenames` | Boolean | `false` | any | Emit a VFAT long-name entry for every file/dir (with a generated 8.3 alias), even names that already fit 8.3 — the way Windows always records a long name. Implies VFAT on. |
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `720 KB (3.5" DD)`, `1.44 MB (3.5" HD)`, `1.68 MB (DMF)`, `2.88 MB (3.5" ED)`, `160 KB (5.25" SS/SD)`, `180 KB (5.25" SS/SD)`, `320 KB (5.25" DS/DD)`, `360 KB (5.25" DS/DD)`, `1.2 MB (5.25" HD)`, `650 MB (CD)`, `700 MB (CD)`, `4.7 GB (DVD-5)`, `8.5 GB (DVD-9)`, `25 GB (BD-SL)`, `50 GB (BD-DL)`, `100 GB (BD-XL)`, `128 GB (BD-XL)`, `15 GB (HD DVD-SL)`, `30 GB (HD DVD-DL)`, `32 MB`, `128 MB`, `512 MB`, `1 GB`, `2 GB`, `4 GB` | Auto sizes the image to exactly hold the files being stored (recommended). Fixed presets match floppy, optical and card formats. |
| `LongFilenames` | Boolean | `true` | any | VFAT LFN entries preserve mixed-case names and names > 8.3 chars. Disable only for strict DOS 8.3 compatibility (no VFAT). |
| `RootEntries` | Enum | `Auto` | `Auto`, `16 (DMF)`, `32`, `64`, `112`, `224`, `512` | Max items in the root directory (FAT12/16 only; FAT32 has no limit). Microsoft DMF Win95 disks used 16 to reclaim those sectors for data. Auto: 224 for FAT12, 512 for FAT16. |
| `TransactionFat` | Boolean | `false` | any | Marks the image for transaction-based FAT updates (Windows Embedded/CE crash-safe style). The marker is the TFAT tag in BS_FilSysType; BS_Reserved1 is left alone, because that is where FAT records an unclean unmount. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 11 chars, ASCII only). |

## Storage methods

- `stored` — Stored

## Further reading

- https://download.microsoft.com/download/1/6/1/161ba512-40e2-4cc9-843a-923143f3456c/fatgen103.doc — Microsoft "FAT32 File System Specification" (FATGEN 1.03), the canonical FAT12/16/32 spec
- https://en.wikipedia.org/wiki/Design_of_the_FAT_file_system — Wikipedia's detailed on-disk reference incl. vendor variants
- https://github.com/torvalds/linux/tree/master/fs/fat — mainline kernel implementation

