# FATX (Xbox) (`Fatx`)

Xbox/Xbox 360 FATX filesystem image (R/W: list/extract/create/add/remove at root; FAT16+FAT32 width-aware).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.fatx` |
| Recognised extensions | `.fatx` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `46 41 54 58` | 0 | 0.95 |

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

By moving what is out of place, through `FatxBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | yes | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### FatxFormatDescriptor

R/W descriptor for Microsoft Xbox / Xbox 360 FATX volumes. Magic "FATX" at offset 0; 4 KiB superblock followed by FAT16/FAT32 table. Read via `FatxReader`, create via `FatxWriter`, mutate via `FatxModifier` (in-place Add/Remove on the root directory; sub-directory mutation stays out of scope). References:

### FatxReader

Reader for Microsoft Xbox / Xbox 360 FATX volumes. On-disk layout (little-endian): +0x000 "FATX" 4 magic +0x004 volume_id 4 +0x008 sectors_per_cluster 4 +0x00C root_dir_cluster 4 +0x010 unused / name 0x1000 - 0x10 FAT immediately follows the superblock at offset 0x1000 (4 KiB). FAT entries are either 16 or 32 bits depending on cluster count; if the cluster count &lt; 0xFFF4 the table is FAT16, otherwise FAT32. EOC sentinels 0xFFF8/0xFFFFFFF8. Directory record (0x40 bytes): +0x00 name_length u8 (0xFF = unused, 0xE5 = deleted) +0x01 attributes u8 +0x02 name 42 bytes (padded 0xFF) +0x2C first_cluster u32 +0x30 size u32 +0x34..0x3F timestamps Spec sources: https://www.eecg.utoronto.ca/~lie/papers/usenix2002.pdf (Xbox security paper) and FreeXboxBios FATX documentation; also reverse-engineered by xboxhdm/fatx-linux/fatxlinux projects.

### FatxWriter

Builds Microsoft Xbox / Xbox 360 FATX filesystem images from scratch per the reverse-engineered FATX spec (FreeXboxBios / fatx-linux / fatxlinux).

On-disk layout (little-endian):

Real Xbox volumes use 16 KiB clusters and FAT32, but the format itself permits any power-of-two sectors-per-cluster — the writer auto-picks a small cluster (2 KiB / 4 sectors) for tiny synthetic images so unit tests stay compact, and 16 KiB / 32 sectors for any image > 1 MiB, matching the original Xbox HDD convention.

FATX dirent names are limited to 42 ASCII bytes — no LFN, no Unicode. Names longer than 42 characters are truncated with a trailing ~N alias to keep them unique within the same directory.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `SectorsPerCluster` | Enum | `0` | `0`, `4`, `8`, `16`, `32`, `64`, `128` | FATX cluster size in 512-byte sectors (0 = auto-optimise for least slack; 32 = 16 KiB Xbox default). |
| `VolumeId` | String | `` | any | 32-bit volume identifier (hex or decimal). Blank picks one, the way formatting does. |

## Storage methods

- `stored` — Stored

## Further reading

- https://xboxdevwiki.net/FATX — Xbox Dev Wiki's FATX page, the de-facto community specification
- https://github.com/mborgerson/fatx — maintained open-source FATX implementation (fatxfs)
- https://en.wikipedia.org/wiki/Design_of_the_FAT_file_system — Wikipedia's FAT reference, which covers the FATX variant

