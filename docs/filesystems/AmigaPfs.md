# Amiga Professional FS (`AmigaPfs`)

Amiga Professional File System (PFS3/PFS3aio) image — Stage 1 R/W (boot block + root + linear dirblock chain + contiguous file extents; anode-as-direct-block convention; in-place Add/Remove against the same shape; full anode-table/bitmap emission deferred — not yet FS-UAE/WinUAE mountable).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.pfs` |
| Recognised extensions | `.pfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `50 46 53 02` | 0 | 0.95 |
| `50 46 53 03` | 0 | 0.95 |
| `50 46 53 61` | 0 | 0.95 |

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

By moving what is out of place, through `AmigaPfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### AmigaPfsFormatDescriptor

R/W descriptor for the Amiga Professional File System (PFS3 / PFS3aio). Signature "PFS\x02"/"PFS\x03"/"PFSa" at offset 0 of the boot block. Stage 1 caveat: only direct-block file references are extractable; multi- block files requiring full anode-tree traversal will report a partial extraction. The reader robustly lists all dirblock entries regardless. Stage 1 writer emits boot + root + linear dirblock chain + contiguous per-file data extents (anode-as-direct-block convention) — self-round-trip clean with the matching reader. Stage 1 R/W (this descriptor) adds in-place Add/Remove against the same shape via `AmigaPfsModifier`; image is still not FS-UAE/WinUAE mountable (full PFS3aio anode-table / bitmap / rootinfo emission deferred to a future Stage 2 promotion). References:

### AmigaPfsReader

Reader for Amiga Professional File System (PFS3 / PFS3aio) — a high performance Amiga filesystem authored by Michiel Pelt &amp; Toni Wilen. On-disk layout (BIG-endian, 512-byte blocks on floppy, configurable on HD): Block 0..1 boot block — first 4 bytes are the disk signature: "PFS\x02" (older PFS2), "PFS\x03" (PFS3) or "PFSa" (PFS3aio). Bytes 2-5 of byte 0..3 of block 0 may also carry "muFS" (multi-user fs variant). For this reader we accept the 3 standard PFS signatures. Root block typically block 80 on a floppy (located via the rootblock pointer in the bootblock). The root block carries: +0 ID 4 bytes "PFS\x02"/"PFS\x03"/"PFSa" +12 rblkcluster u16 +14 blocknr u32 +18 datestamp u32 +22 options u32 +26 diskname 32 bytes (null-padded ASCII) +60 rootinfo (anode pointers) Subsequent fields point to "anode blocks" and "dirblocks". PFS uses a tree of "anodes" — each 4-byte allocation entry pointing to a next block in a file or to the next anode in the chain. A directory is a linked list of "dirblocks", each containing variable-length entries with the filename, anode number, file size, and protection bits. This Stage 1 reader walks the bootblock + root block, identifies the first dirblock chain, and extracts simple file entries that fit in a single block reference (no fragmented file traversal across multiple anodes). Real-world PFS3 multi-block files require full anode-tree traversal which is deferred to Stage 2. Spec source: https://github.com/tonioni/AmigaPFS — public PFS3aio reference implementation; Michiel Pelt's original PFS Technical Note (1995).

### AmigaPfsWriter

Writer for Amiga Professional File System (PFS3) images. Emits the same on-disk shape `AmigaPfsReader` parses: PFS3aio (Toni Wilen's reference implementation) and Michiel Pelt's original 1995 PFS technical note describe far richer on-disk structures (anode tables, root-info pointers to anode/dir B-trees, bitmap blocks, deldir, rblkcluster groups). The Stage 1 reader explicitly does not walk those structures, so the writer's output is intentionally a Stage 1 skeleton: signature + root block + linear dirblock chain + contiguous file extents. It is sufficient for self-round-trip with the matching reader and for descriptors that exercise the WORM `Create` path. It is not mountable in FS-UAE / WinUAE — full anode/bitmap emission would be required for emulator parity and is deferred to a Stage 2 promotion.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 31 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/tonioni/pfs3aio — PFS3 All-In-One source (Toni Wilen), the canonical open-source PFS3 on-disk implementation
- Professional File System 3 by Michiel Pelt (original Aminet release + documentation)
- https://en.wikipedia.org/wiki/Professional_File_System — Wikipedia overview

