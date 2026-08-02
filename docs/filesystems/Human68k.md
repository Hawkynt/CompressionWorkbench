# Sharp X68000 Human68k (`Human68k`)

Sharp X68000 Human68k FAT-derived filesystem with Shift_JIS filenames; identified by 'X68K' tag at boot offset 0x10.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.2hd` |
| Recognised extensions | `.2hd`, `.dim` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `58 36 38 4B` | 16 | 0.90 |

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

By moving what is out of place, through `Human68kBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Human68kFormatDescriptor

Descriptor for Sharp X68000 Human68k disk images. The Human68k filesystem is FAT12-derived with Shift_JIS filenames and an "X68K" identifier at boot-sector offset 0x10. Recognised extension is `.dim` (Disk Image Manager); the `.hdf` extension that Human68k historically used is intentionally NOT claimed here — it collides with the more-common HDF4 scientific data format, which owns `.hdf` in this registry.

Human68k supports subdirectories per the FAT12 model; the current minimal writer emits a single flat root directory only — hierarchical writes are deferred. The reader handles subdirectory dirents at the root by surfacing them as entries with `IsDirectory` set, but does not recurse into them (kept honest in the descriptor capabilities).

Capabilities: read + write (flat-only writer), defragment via extract-and-rebuild, free-space wiping driven by the extent map, and creation-options schema for bytes-per-sector / sectors-per-cluster / total-sectors / volume label.

References:

### Human68kReader

Reads Sharp X68000 Human68k disk images. Human68k uses a FAT12-like filesystem with an extended Shift_JIS-aware directory record format — Japanese file names are stored in Shift_JIS, and the BPB at offset 0x10 carries a Human68k-specific identifier.

Boot sector layout (little-endian, sector 0): 0x00 byte[3] jump (0x60 or 0xEB or 0xE9) 0x03 char[8] OEM name — Human68k disks typically carry "X68K" at offset 0x10, but many images put OEM at offset 0x03 0x0B u16 bytes per sector 0x0D byte sectors per cluster 0x0E u16 reserved sector count 0x10 char[4] "X68K" tag (Human68k identifier — primary detection magic) 0x14 byte number of FATs 0x15 u16 root directory entry count 0x17 u16 total sectors (small) 0x19 byte media descriptor 0x1A u16 sectors per FAT 0x1C u16 sectors per track 0x1E u16 heads 0x20 u32 hidden sectors

Directory entry layout (32 bytes; same as DOS FAT12 with attributes, but filename can use Shift_JIS encoding): 0x00 char[8] filename 0x08 char[3] extension 0x0B byte attributes (0x10=dir, 0x08=volume label, 0x80=killed) 0x1A u16 first cluster 0x1C u32 file size

### Human68kWriter

Builds a fresh Sharp X68000 Human68k disk image from scratch. The format is FAT12-derived: a BIOS Parameter Block at sector 0 with an extra "X68K" identifier at offset 0x10 (Human68k's primary detection magic), one or two FAT12 copies, a fixed-size root directory, and a data area of N clusters.

The writer uses Shift_JIS-aware short-name encoding (filenames are stored as raw byte sequences in the dirent at offsets 0..7 for the 8-char name and 8..10 for the 3-char extension). Non-ASCII bytes pass through; Shift_JIS decoding is the reader's responsibility.

The image lays out as: boot sector (1 sector), FAT (sectorsPerFat sectors), root directory (ceil(rootEntries*32 / bytesPerSector) sectors), then data clusters. Single FAT only (FatCount=1) to keep the minimal image small.

### Human68kExtentMap

Enumerates the on-disk byte layout of a Human68k disk image: boot sector, FAT(s), and root directory are emitted as `MetadataReserved`; every file's first cluster + size is collapsed into one `Used` extent (Human68k's reader currently surfaces only the first contiguous run); unattributed sectors are left for the caller to fill as `Free`.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BytesPerSector` | Enum | `512` | `256`, `512`, `1024` | Bytes per sector. 512 is the default and safest choice for round-tripping with the reader. |
| `SectorsPerCluster` | Enum | `Auto` | `Auto`, `1 B`, `2 B`, `4 B`, `8 B`, `16 B` | Sectors per cluster (1, 2, 4, 8 or 16). Auto picks the smallest that fits the file set with <= 5 % slack. |
| `TotalSectors` | Integer | `0` | any | Total sector count. 0 = auto (sized to fit the file set + minimum metadata). |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 11 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- Sharp / Hudson Soft "Human68k" manuals — the original vendor documentation of the FAT12-derived filesystem
- https://en.wikipedia.org/wiki/Human68k — Wikipedia overview
- https://en.wikipedia.org/wiki/Sharp_X68000 — Wikipedia overview of the host platform

