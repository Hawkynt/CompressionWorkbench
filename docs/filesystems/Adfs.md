# Acorn ADFS (`Adfs`)

Acorn ADFS (BBC Micro / Archimedes / RISC OS) filesystem — read + R/W (ADFS-L variant; in-place Add/Remove against the old-map FSM and Hugo-bracketed root directory).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.adl` |
| Recognised extensions | `.adl`, `.adf` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `48 75 67 6F` | 512 | 0.75 |
| `4E 69 63 6B` | 512 | 0.75 |
| `48 75 67 6F` | 1024 | 0.70 |
| `4E 69 63 6B` | 1024 | 0.70 |
| `0A 00 00 00 0D 0A 00 00 00 01` | 4 | 0.80 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | yes | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `AdfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### AdfsFormatDescriptor

Descriptor for Acorn Advanced Disc Filing System (ADFS) images. Read works for both old-map (S/M/L, 256-byte sectors) and new-map (D/E/F, 1024-byte sectors, fragment-mapped). Create emits a new-map volume by default, which is the layout a real ADFS driver mounts — Linux's has no code path for an old map at all; pass `Variant=old` for the ADFS-L 640 KB layout. Detected by the "Hugo" or "Nick" directory marker at sector 2 — root dir magic at file offset 0x200 (old map) or 0x400 (new map). References:

### AdfsReader

Reader for Acorn Advanced Disc Filing System (ADFS) "old map" image formats (ADFS-S, ADFS-M, ADFS-L). Sector size = 256 bytes. The root directory is at sector 2 (file offset 0x200). Each directory is 1280 bytes (5 sectors) and bracketed by a 4-byte "Hugo" or "Nick" marker at the start (DirHdr) and matching marker just before the directory tail. Directory layout (per https://mdfs.net/Docs/Comp/Disk/Format/ADFS, originally published in the BBC Master Reference Manual): +0x000 StartName 1 byte 'H' (=0x48) — start of "Hugo" magic +0x000 "Hugo" 4 bytes (master/L variant) or "Nick" 4 bytes +0x005 DirEntries 47 entries x 26 bytes = 1222 bytes +0x4CB EndName "Hugo" again +0x4CF DirName 10-byte master sequence name (parent ref) +0x4D9 ParentInd 3-byte parent directory sector +0x4DC DirTitle 19-byte ASCII title +0x4EF Reserved 14 bytes +0x4FD EndCheckByte 1 byte Each 26-byte directory entry: +0x00 Name 10 bytes (top bit of byte 0 = attribute flag) +0x0A LoadAddr 4 bytes (LE) +0x0E ExecAddr 4 bytes (LE) +0x12 Length 4 bytes (LE) +0x16 IndCyl 3 bytes (start sector, LE) +0x19 CycleCount 1 byte (sequence #) Attributes are encoded in the high bits of the name characters (R=byte0, W=byte1, L=byte2, D=byte3, E=byte4, r=byte5, w=byte6, e=byte7, P=byte8). D = directory. We support the "old map" (S/M/L) variant by default; the newer D/E/F variants use 1024-byte sectors and a different free-space map but the directory layout is similar. Format auto-detected via the "Hugo" marker.

### AdfsWriter

Builds a fresh Acorn ADFS "old-map" disk image (Write-Once-Read-Many).

Targets the ADFS-L variant (640 KiB, 80-track double-sided floppy, 256-byte sectors, 2 560 sectors total). The on-disk layout is:

Layout reference: BBC Master Reference Manual, Section "ADFS Disc Format"; also https://mdfs.net/Docs/Comp/Disk/Format/ADFS. We emit the Acorn-canonical check byte (rotate-and-add over bytes 0..0xFE) so the Linux ADFS kernel driver accepts the image when mounted read-only. ADFS-D/E/F (new-map, 1024-byte sectors) are out of scope for this writer.

### AdfsExtentMap

Describes where an old-map ADFS disc keeps its bytes: the two free-space map sectors, the root directory, and each file's run of sectors.

An old-map ADFS file is one contiguous run. Its directory entry carries the sector it starts at and its length in bytes, which is the whole of what says where it is — so a run can be moved and the entry rewritten.

New-map discs are not described here. There a file is a fragment identifier resolved through a zone bitmap, and neither the fragment's position nor its length is written down anywhere a move could rewrite.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Variant` | Enum | `new` | `new`, `old` | new = the E/F-style map a real ADFS driver mounts; old = the S/M/L free-space-list layout of a 640 KB ADFS-L floppy. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 19 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- Acorn "Advanced Disc Filing System User Guide" (Acorn Computers) — the original vendor format documentation
- RISC OS Programmer's Reference Manual, FileCore chapter — new-map (D/E/F) on-disk structures
- https://en.wikipedia.org/wiki/Advanced_Disc_Filing_System — Wikipedia overview of the ADFS variants

