# TRSDOS / LDOS (`Trsdos`)

TRSDOS / LDOS disk filesystem (TRS-80 Model I/III/4) — flat-only GAT+HIT layout at track 17, 256-byte sectors.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.trsdos` |
| Recognised extensions | `.trsdos`, `.dmk`, `.jv1`, `.jv3` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `FE` | 78413 | 0.55 |

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

By moving what is out of place, through `TrsdosBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### TrsdosFormatDescriptor

Descriptor for TRSDOS / LDOS disk images (Radio Shack TRS-80 Model I / III / 4, late 1970s–early 1980s). Detection by the 0xFE GAT signature at track 17, sector 0, offset 0xCD; reader walks the directory records that follow in track 17 sectors 2..N. Sector size is 256 B; default geometry is 40 tracks × 18 spt (Model III/4 DD) with fallback to 10/9/26 spt.

TRSDOS is a flat-only filesystem (no subdirectories). The `List` output therefore never contains a directory entry.

Capabilities: read + write, defragment via extract-and-rebuild, free-space wiping driven by the extent map, and creation-options schema for density/track-count selection.

References:

### TrsdosReader

Reads TRSDOS / LDOS disk images (Radio Shack TRS-80 Model I/III/4). TRSDOS organises a fixed 35-track / 40-track / 80-track disk into "granules" (groups of sectors) tracked by the Granule Allocation Table (GAT) and an associated Hash Index Table (HIT) at track 17.

Per the TRSDOS specification: - Track 17 is the directory track. Sector 0 of track 17 holds the GAT; sector 1 holds the HIT. - GAT byte at offset 0xCD = 0xFE identifies a TRSDOS-formatted disk. - Sectors 2..N of track 17 hold 32-byte directory records. Each record begins with an attribute byte; 0x00 = unused, 0x10 = system, 0x40 = invisible, 0x80 = killed. - Filename is 8 ASCII characters (offset 5..12), extension is 3 ASCII characters (offset 13..15). End-of-file byte count is at offset 30 (high byte) + offset 27 (low byte); sector count at offset 28..29 (little-endian).

Sector size is 256 bytes; sectors-per-track defaults to 10 (DD) but JV3/DMK images may report 18 SD or 36 DD. We assume 256-byte sectors with 18 sectors/track DD geometry (track 17 starts at file offset 17 * 18 * 256 = 78336) which matches the most common Model III/4 disks.

### TrsdosWriter

Builds a fresh TRSDOS / LDOS disk image from scratch (Write-Once, Read-Many). The format places the GAT (Granule Allocation Table) at track 17, sector 0; the HIT (Hash Index Table) at track 17, sector 1; and 32-byte directory records at sectors 2..N of track 17. Tracks outside 17 hold file data, allocated in 5-sector "granules" per the Model III/4 convention.

This writer produces the canonical Model III/4 18-sectors/track double-density geometry (256-byte sectors). Track count and density drive the total image size. The directory holds at most `MaxDirectoryRecords` entries.

Each directory record carries:

### TrsdosExtentMap

Enumerates the on-disk byte layout of a TRSDOS / LDOS disk image. Track 17 (GAT + HIT + directory records) is emitted as `MetadataReserved`; every file's sector run is one `Used` extent; unattributed sectors are left for the caller to fill as `Free`.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Date` | String | `01/01/26` | any | 8-character format date written to the GAT (MM/DD/YY). |
| `Density` | Enum | `Auto` | `Auto`, `Single`, `Double` | Single density = 10 sectors/track. Double density = 18 sectors/track. |
| `DiskName` | String | `WORM` | any | 8-character disk name written to the GAT (truncated/padded). |
| `Tracks` | Enum | `Auto` | `Auto`, `35`, `40`, `80` | 35 = Model I 5.25" SD. 40 = Model III/4 DD. 80 = Model 4 high-density. |

## Storage methods

- `stored` — Stored

## Further reading

- Roy Soltoff, "The Programmer's Guide to LDOS/TRSDOS Version 6" — canonical GAT/directory documentation
- https://www.tim-mann.org/trs80.html — Tim Mann's TRS-80 resources (xtrs emulator + format notes)
- https://en.wikipedia.org/wiki/TRSDOS — Wikipedia article

