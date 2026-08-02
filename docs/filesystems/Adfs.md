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
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### AdfsFormatDescriptor

Descriptor for Acorn Advanced Disc Filing System (ADFS) images. Read works for both old-map (S/M/L, 256-byte sectors) and new-map (D/E/F, 1024-byte sectors, fragment-mapped). Create emits a new-map volume by default, which is the layout a real ADFS driver mounts — Linux's has no code path for an old map at all; pass `Variant=old` for the ADFS-L 640 KB layout. Detected by the "Hugo" or "Nick" directory marker at sector 2 — root dir magic at file offset 0x200 (old map) or 0x400 (new map). References:

### AdfsReader

Reader for Acorn Advanced Disc Filing System (ADFS) "old map" image formats (ADFS-S, ADFS-M, ADFS-L). Sector size = 256 bytes. The root directory is at sector 2 (file offset 0x200). Each directory is 1280 bytes (5 sectors) and bracketed by a 4-byte "Hugo" or "Nick" marker at the start (DirHdr) and matching marker just before the directory tail. Directory layout (per https://mdfs.net/Docs/Comp/Disk/Format/ADFS, originally published in the BBC Master Reference Manual): +0x000 StartName 1 byte 'H' (=0x48) — start of "Hugo" magic +0x000 "Hugo" 4 bytes (master/L variant) or "Nick" 4 bytes +0x005 DirEntries 47 entries x 26 bytes = 1222 bytes +0x4CB EndName "Hugo" again +0x4CF DirName 10-byte master sequence name (parent ref) +0x4D9 ParentInd 3-byte parent directory sector +0x4DC DirTitle 19-byte ASCII title +0x4EF Reserved 14 bytes +0x4FD EndCheckByte 1 byte Each 26-byte directory entry: +0x00 Name 10 bytes (top bit of byte 0 = attribute flag) +0x0A LoadAddr 4 bytes (LE) +0x0E ExecAddr 4 bytes (LE) +0x12 Length 4 bytes (LE) +0x16 IndCyl 3 bytes (start sector, LE) +0x19 CycleCount 1 byte (sequence #) Attributes are encoded in the high bits of the name characters (R=byte0, W=byte1, L=byte2, D=byte3, E=byte4, r=byte5, w=byte6, e=byte7, P=byte8). D = directory. We support the "old map" (S/M/L) variant by default; the newer D/E/F variants use 1024-byte sectors and a different free-space map but the directory layout is similar. Format auto-detected via the "Hugo" marker.

### AdfsNewMapWriter

Builds an Acorn ADFS new-map image — the E/F-style layout, as opposed to the S/M/L free-space-list layout `AdfsWriter` emits.

Why this exists. Linux's adfs driver only mounts new-map discs: it looks for a disc record either in the boot block at 0xC00 + 0x1C0 or at sector 0 + 4 with a single zone, and its allocation walk expects the zone bitmap described below. An old-map ADFS-L image has neither, so the driver cannot read one at all — which is what this writer fixes.

Layout. One 1024-byte sector per block, one map zone, one map bit per sector:

Bounds. A single zone's bitmap is one sector — 8192 bits, of which 512 are the header and disc record — so a volume holds at most `MaxSectors` sectors (7.5 MB), and every fragment costs at least idlen + 1 sectors. Multi-zone maps, share offsets for small files, and F+ big directories are out of scope.

Cross-checked against the kernel's fs/adfs: adfs_checkdiscrecord, adfs_validate_dr0, adfs_map_layout, lookup_zone, scan_free_map, adfs_calczonecheck and adfs_dir_checkbyte.

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

