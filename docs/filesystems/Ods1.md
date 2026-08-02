# ODS-1 (VAX/VMS Files-11 L1) (`Ods1`)

DEC ODS-1 (RSX-11/VAX-VMS Files-11 Level 1) volume — read + R/W create + in-place Add/Remove (Stage 1: single-extent retrieval pointers, ASCII filenames, ≤ 9.3 chars, 64-slot INDEXF window, home-block additive checksums recomputed on every mutation).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.ods1` |
| Recognised extensions | `.ods1`, `.vms` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `44 45 43 46 49 4C 45 31 31 41` | 1008 | 0.95 |

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

By moving what is out of place, through `Ods1BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Ods1FormatDescriptor

Read+R/W descriptor for DEC VAX/VMS ODS-1 (Files-11 Level 1) volumes. Signature "DECFILE11A" at file offset 0x3F0 (= LBN 1 + 0x1F0). Reader covers single-extent retrieval pointers; writer emits a fresh Files-11 L1 disk image (home block + index file + bitmap + user-file headers + contiguous extents); modifier mutates existing images in-place via `Ods1Modifier` (Add allocates a free header slot + a contiguous BITMAP run, Remove zeros the header slot + frees its BITMAP bits + zero-fills its data extent; both recompute the home-block additive checksums). Self-round-trip gated; no Linux fsck for ODS-1 exists. References:

### Ods1Reader

Reader for the DEC VAX/VMS ODS-1 (Files-11 Level 1) filesystem (1977-1984, predecessor of ODS-2). ODS-1 was originally designed for RSX-11M and migrated to VAX/VMS V1.0. Blocks are 512 bytes ("LBN" = Logical Block Number). Files are described by 512-byte file headers stored in the INDEXF.SYS system file (file ID 1,1). On-disk layout (little-endian): LBN 0 boot block (variable) LBN 1 home block (512 bytes) — volume superblock +0x000 hm1$w_ibmapsize u16 +0x002 hm1$l_ibmaplbn u32 first LBN of allocation bitmap +0x006 hm1$w_maxfiles u16 +0x008 hm1$w_cluster u16 +0x00A hm1$w_devtype u16 +0x00C hm1$w_structlev u16 Files-11 level (=257 for ODS-1) +0x00E hm1$t_volname 12 ASCII volume name +0x01C hm1$w_volowner 4 uic +0x020 hm1$w_protect 2 +0x022 hm1$w_volchar 2 +0x024 hm1$w_fileprot 2 +0x026 hm1$b_reserved 6 +0x02C hm1$w_checksum1 2 first half checksum +0x02E hm1$t_credate 14 +0x03C hm1$b_window 1 +0x03D hm1$b_lru_lim 1 +0x03E hm1$w_extend 2 +0x040 ... +0x1F0 hm1$t_format "DECFILE11A" (12 bytes) +0x1FE hm1$w_checksum2 2 second half checksum File header (512 bytes): +0x00 fh1$b_idoffset u8 offset (in words) of ident area +0x01 fh1$b_mpoffset u8 offset (in words) of map area +0x02 fh1$w_fid_num u16 file number +0x04 fh1$w_fid_seq u16 sequence +0x06 fh1$w_struclev u16 +0x08 fh1$w_fid_volume u16 +0x0A fh1$b_filechar 1 F11_DIRECTORY = 0x40 ... ident area: fh1$t_filename (9 bytes Radix-50 = 6 ASCII chars) + fh1$t_filetype (3 Radix-50 = 3 chars) + version map area: retrieval pointers — each 4 bytes: u16 count + u16 high_lbn (24-bit LBN low in high byte field) For simplicity Stage-1 reader assumes "format 1" pointers: u16 count + u16 hi + u16 lo Spec source: VAX/VMS V4 documentation set "VAX/VMS File Definition Language Facility Reference Manual"; OpenVMS Documentation "Files-11 On-Disk Structure Specification" (1986 reprint covers both Level 1 and Level 2).

### Ods1Writer

Writer for DEC VAX/VMS ODS-1 (Files-11 Level 1) disk images. Produces a minimal but spec-shaped Files-11 volume that the companion `Ods1Reader` can round-trip cleanly.

Layout produced (LBN = 512-byte Logical Block Number):

`LBN 0 boot block (zero-filled, no PDP-11 bootstrap) LBN 1 home block — DECFILE11A signature, volume name, INDEXF LBN LBN 2 BITMAP.SYS data — allocation bitmap (1 LBN fits ≤ 4096 LBNs) LBN 3 pad / spare LBN 4..67 index-file window (64 LBNs) — one 512-byte file header per user file (file id 1..N), remaining slots zero-filled LBN 68.. contiguous data extents in allocation order`

The writer matches the existing `Ods1Reader` Stage-1 encoding exactly: filenames as raw ASCII (not Radix-50), retrieval pointers in the simplified (count-1, hi, lo) form, file size reported as block-count × 512 (no sub-block fh1$l_efblk). Real VAX/VMS images use Radix-50 and an end-of-file block field; Stage-1 of this format is a pragmatic round-trip-clean subset. The on-disk shape (boot/home/bitmap/index-file layout, DECFILE11A signature at the canonical home-block offset, file headers with idoff/mpoff/fileNum, little-endian everything) follows the Files-11 spec.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 12 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- DEC "Files-11 On-Disk Structure Specification" — the canonical ODS-1/ODS-2 spec (archived at Bitsavers)
- https://en.wikipedia.org/wiki/Files-11 — Wikipedia article

