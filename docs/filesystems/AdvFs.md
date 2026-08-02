# AdvFS (Tru64 UNIX) (`AdvFs`)

AdvFS (Tru64 UNIX Advanced File System) image — header parse + WORM emit of a clean-room storage-domain layout (RBMT page 0 cookie + DMN/VD/MATTR fields + AdvFS-WB file-table extension).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.advfs` |
| Recognised extensions | `.advfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `41 44 56 46 53 00 52 42 4D 54 30 00 00 00 00 00` | 131072 | 0.80 |

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

By moving what is out of place, through `AdvFsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### AdvFsFormatDescriptor

Read-only descriptor for AdvFS (Tru64 UNIX Advanced File System, DEC/HP). Open-sourced by HP in 2008 under the GPL; the storage domain → file set → file model and the on-disk structures are described in `bs_ods.h`, `bs_disk_block.h`, and `bs_public.h` of that release. Walking the BMT (Bitfile Metadata Table) B-tree and following BFD (Bitfile Descriptor) extent chains to extract user files is explicitly out of scope (multi-week effort) — this descriptor surfaces: Detection: a 16-byte cookie `"ADVFS\0RBMT0\0\0\0\0\0"` at offset 131072 (= page 16 × 8192-byte AdvFS page). This is an internal convention rather than the canonical Tru64 on-disk magic (record type discriminators rather than a fixed bytes-at-offset signature). Real Tru64 images that don't carry the cookie will not auto-detect but can still be parsed when fed to the descriptor directly. Create / Modify: a clean-room AdvFS-WB storage-domain layout with a flat file table inside RBMT page 0; `AdvFsInPlaceModifier` performs genuine in-place add/replace/remove against that table. References:

### AdvFsReader

Parses the AdvFS (Tru64 UNIX Advanced File System) on-disk volume header. AdvFS was open-sourced by HP in 2008 (`https://sourceforge.net/projects/advfs/`); the on-disk layout below is taken from `bs_ods.h`, `bs_disk_block.h`, and `bs_public.h` in that release. AdvFS layout summary: Detection magic: this descriptor synthesises a 16-byte cookie at offset `131072` (= page 16 × 8192) — the start of the AdvFS RBMT page 0 — using the literal ASCII tag `"ADVFS\0RBMT0\0\0\0\0\0"`. This is an internal convention since the HP source release uses record-type discriminators (`BSR_VD_ATTR` = 13, `BSR_DMN_ATTR` = 14, `BSR_DMN_MATTR` = 15) rather than a single bytes-at-offset magic. Real Tru64 images that don't carry this tag will not be detected automatically but can still be inspected once the file is fed to the descriptor directly. References:

### AdvFsWriter

Builds minimal AdvFS (Tru64 UNIX) volume images that round-trip cleanly through `AdvFsReader`. The on-disk layout is a clean-room subset of the HP-2008 open-sourced AdvFS storage-domain model: bootstrap pages 0..15 are zero, RBMT page 0 starts at byte offset `131072` with the 16-byte detection cookie `"ADVFS\0RBMT0\0\0\0\0\0"` followed by the `BSR_DMN_ATTR` / `BSR_VD_ATTR` / `BSR_DMN_MATTR` field bundle the reader documents. A trailing AdvFS-WB file table extension (eyecatcher `"ADVFSWBFT\0\0\0\0\0\0\0"`) follows the volume tag; the reader picks it up when present so file payloads survive a write→read round-trip.

This is honestly scoped: walking the real BMT B-tree to reconstruct user files from an arbitrary Tru64 image is multi-week work — we don't claim to. What this writer does claim is a self-consistent, deterministic image whose reader counterpart recovers every byte of every file. The layout intentionally shares the cookie + DMN/VD/MATTR field order with the existing read path so the descriptor's detection magic still matches, the metadata.ini still parses, and the rbmt_page0.bin capture still surfaces the documented fields.

Per-file storage: each file's payload is appended to a continuous data area that begins at the first 8 KB page boundary after the RBMT page (offset 139264 = 17 × 8192). The file table inside RBMT page 0 records (name length, name, payload offset, payload length) triples; payload bytes are stored as-is, no compression. Names are UTF-8, capped at 255 bytes.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 63 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- FULL.advfs — the raw image bytes
- metadata.ini — parsed BSR_DMN_ATTR/BSR_VD_ATTR/BSR_DMN_MATTR fields
- rbmt_page0.bin — 4 KB capture of RBMT page 0 (offset 131072)
- https://sourceforge.net/projects/advfs/ — HP 2008 GPL release
- HP "AdvFS Technical Reference" (in the source tarball)
- Wikipedia "Advanced File System"

