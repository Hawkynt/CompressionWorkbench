# Cromemco RDOS (`Cromemco`)

Cromemco RDOS Z-80 disk filesystem — CP/M-derived flat-only 8.3 filenames, 128-byte sectors.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.rdos` |
| Recognised extensions | `.rdos`, `.crom` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `43 52 4F 4D 45 4D 43 4F` | 11 | 0.90 |

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

By moving what is out of place, through `CromemcoBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### CromemcoFormatDescriptor

Descriptor for Cromemco RDOS volumes (Z-80 CP/M-derived system used on Cromemco Z2 / System Three machines, late 1970s). Detection by the 0xC3 (Z-80 JP) prefix at offset 0 plus an embedded "CROMEMCO" ASCII tag inside the first 64 bytes of the boot block.

RDOS is a flat-only filesystem (CP/M-style): all entries live in a single 16-sector directory area starting at sector 2 with no support for subdirectories. The `List` output therefore never contains a directory entry.

Capabilities: read + write (write-once, no in-place add/remove), defragment via extract-and-rebuild, free-space wiping driven by the extent map, and creation-options schema for density/track-count selection through the Convert Archive dialog.

References:

### CromemcoReader

Reads Cromemco RDOS (Z-80 system disk) volumes. RDOS is a CP/M-like filesystem with 8.3 filenames, 128-byte sectors, and a fixed directory area in the system tracks. The bootblock at block 0 starts with a JP-instruction (0xC3 low high) followed by an embedded "CROMEMCO" ASCII tag that identifies the volume.

Bootblock layout (block 0, little-endian; first 32 bytes): 0x00 byte 0xC3 (Z-80 JP instruction) 0x01 u16 entry-point address 0x03 char[8] reserved (zero-padded) 0x0B char[8] "CROMEMCO" ASCII (signature; may also appear at varying offsets up to 0x40 in late RDOS variants — we scan the first 64 bytes)

Directory entry layout (32 bytes; back-to-back in the directory area starting at sector 2 = file offset 0x100): 0x00 byte user code (0xE5 = deleted) 0x01 char[8] filename (space-padded ASCII) 0x09 char[3] extension (space-padded ASCII) 0x0C u16 start block (LE) 0x0E u16 length in 128-byte records (LE) 0x10..0x1F reserved

### CromemcoWriter

Builds a fresh Cromemco RDOS disk image from scratch (Write-Once, Read-Many). The format is CP/M-derived: a boot block at sector 0 (with the 0xC3 JP-instruction prefix and an embedded "CROMEMCO" tag at offset 0x0B), a flat directory area starting at sector 2 with 32-byte entries, and data blocks immediately following.

Geometry knobs:

The reader hard-codes `SectorSize` at 128 bytes, so this writer always emits 128-byte sectors. Track count drives the total image size. Maximum entries per disk is `MaxEntries` (64); attempting to add more throws.

### CromemcoExtentMap

Enumerates the on-disk byte layout of a Cromemco RDOS image: the boot block (sector 0) and directory area (sectors 2..17) are emitted as `MetadataReserved`, each file is one contiguous `Used` run, and any sector not covered is left for the caller to fill as `Free`.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Density` | Enum | `Auto` | `Auto`, `Single`, `Double` | Single density = 18 sectors/track. Double density = 26 sectors/track (System Three). |
| `SectorSize` | Enum | `128` | `128` | Cromemco RDOS uses 128-byte sectors (CP/M convention). |
| `Tracks` | Enum | `Auto` | `Auto`, `35`, `77` | 35 tracks = original Cromemco Z2 floppy. 77 tracks = System Three drives. |

## Storage methods

- `stored` — Stored

## Further reading

- Cromemco RDOS Instruction Manual (Cromemco Inc.) — the original vendor documentation
- https://bitsavers.org/pdf/cromemco/ — Bitsavers' scanned Cromemco manual archive
- https://en.wikipedia.org/wiki/Cromemco — Wikipedia overview of the machines

