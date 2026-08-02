# GEMDOS (Atari ST) (`Gemdos`)

Atari ST GEMDOS — FAT12 variant with 0x60 BRA.S jump byte.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.st` |
| Recognised extensions | `.st`, `.stx`, `.dim` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `60 00 00 00` | 0 | 0.55 |

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

### GemdosFormatDescriptor

Atari ST GEMDOS disk image descriptor. GEMDOS is a FAT12 variant: the on-disk layout is exactly MS-DOS FAT12 (BPB at offset 11 onwards, two FAT copies, fixed-size root directory, 8.3 dirents, free-block-chain allocation), but the jump byte at offset 0 is `0x60` (Motorola 68000 `BRA.S`) instead of `0xEB`/`0xE9` (x86 `JMP`). The reader and writer here delegate to the FAT12 implementation in `Fat` and re-present the jump byte at the boundary.

Hierarchy support. GEMDOS supports subdirectories via standard FAT12 directory entries (attribute bit 4 = 0x10). The reader / writer inherit full tree support from `FatReader` / `FatWriter`.

Defrag / Purge / Conversion. Driven by the rebuild-based pattern in `DefragRebuilder`; conversion is unlocked for free via `IArchiveCreatable`. Purge zeros all free clusters + cluster-tip slack via the FAT extent map.

Spec. Atari ST Internals (Brückmann, Englisch, Gerits, 1986), GEMDOS disk format chapter; standard FAT12 spec (FATGEN103) for the BPB and on-disk layout.

References:

### GemdosReader

Reads GEMDOS (Atari ST FAT12) images. The on-disk layout is exactly FAT12 except for the jump byte at offset 0 (0x60 BRA.S vs MS-DOS's 0xEB). This reader patches the jump byte to 0xEB in an in-memory copy and then defers to `FatReader` for all parsing — same FAT chains, same root directory, same 8.3 dirent layout.

### GemdosWriter

Builds Atari ST GEMDOS disk images by delegating to `FatWriter` (which emits a spec-compliant FAT12 BPB) and then patching the jump byte at offset 0 from MS-DOS's `0xEB` (x86 `JMP`) to Atari's `0x60` (m68k `BRA.S`). All other BPB fields use the FAT spec layout. The result is a byte-identical GEMDOS volume: same boot-sector size, same FAT chains, same root directory, same data-cluster layout.

### GemdosExtentMap

On-disk layout walker for GEMDOS images. Delegates to FAT12's extent map after re-presenting the GEMDOS jump byte (0x60) as MS-DOS's (0xEB) so the FAT walker accepts the boot sector.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BytesPerSector` | Enum | `512` | `256`, `512`, `1024` | Atari TOS accepts 256 / 512 / 1024 bytes per sector. 512 is universal across emulators and real hardware. |
| `RootEntries` | Enum | `112` | `64`, `112`, `224` | Maximum directory entries in the root directory (FAT12 root is a fixed-size region). |
| `SectorsPerCluster` | Enum | `2` | `1`, `2`, `4` | Allocation unit size in sectors. Two-sector clusters are the GEMDOS default for floppy media. |
| `TotalSectors` | Enum | `1440` | `720`, `1440`, `2880`, `5760` | Total sectors. 720 = 360 KB SS DD, 1440 = 720 KB DS DD, 2880 = 1.44 MB DS HD, 5760 = 2.88 MB DS ED. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 11 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- "Atari ST Internals" (Brückmann, Englisch, Gerits; Abacus/Data Becker, 1986) — GEMDOS disk format chapter, the canonical reference
- https://download.microsoft.com/download/1/6/1/161ba512-40e2-4cc9-843a-923143f3456c/fatgen103.doc — Microsoft FATGEN 1.03, the underlying FAT12 layout
- https://en.wikipedia.org/wiki/GEMDOS — Wikipedia overview

