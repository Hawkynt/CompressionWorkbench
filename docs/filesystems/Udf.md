# UDF (`Udf`)

Universal Disk Format

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.udf` |
| Recognised extensions | `.udf` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `42 45 41 30 31` | 32769 | 0.90 |
| `4E 53 52 30 32` | 34817 | 0.90 |
| `4E 53 52 30 33` | 34817 | 0.90 |
| `4E 53 52 30 32` | 32769 | 0.90 |
| `4E 53 52 30 33` | 32769 | 0.90 |

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
| move blocks | yes | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `UdfBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### UdfFormatDescriptor

R/W descriptor for UDF 2.01 (Universal Disk Format) volume images per ECMA-167 and the OSTA UDF profile. References:

### UdfWriter

Writes a minimal UDF 1.02 filesystem image (ECMA-167). Builds a real directory tree from slash-separated file paths, short allocation descriptors. Computes ECMA-167 §7.2.1 DescriptorCRC (CRC-16/CCITT, init=0, poly=0x1021, non-reflected) and TagChecksum for every descriptor tag so that strict readers (xorriso, Linux udf.ko, mkudffs fsck) accept the produced images. Layout: `Sectors 0-15: System area Sector 16: VRS BEA01 Sector 17: VRS NSR02 Sector 18: VRS TEA01 Sector 32-35: Main VDS (PVD + Partition + LVD + Terminator) Sector 256: AVDP Sector 257: Partition start: File Set Descriptor (FSD) at LBN 0 Sector 258: Root directory File Entry at LBN 1 Sector 259+: Per-node File Entries, directory FID data, file data` A directory's data is a sequence of File Identifier Descriptors (FID, tag 257). The first FID of every directory is the parent entry (Parent flag 0x08, zero-length identifier, ICB pointing at the parent FE). Every directory and file is a File Entry (FE, tag 261); directories carry file type 4, regular files file type 5. A subdirectory FID carries the Directory flag 0x02 and points at the child directory's FE.

### UdfExtentMap

Walks a UDF (ECMA-167) image and yields its actual on-disk byte layout — the 32 KiB system area, NSR02/03 VRS sector, AVDP (LBA 256), the Volume Descriptor Sequence (PD/LVD/etc.), the FSD, the root File Entry, and every file's allocation descriptors as Used extents. Each File Entry's short_ad / long_ad descriptor list yields one extent per descriptor — already-coalesced as the ECMA-167 spec mandates contiguous physical blocks per descriptor.

Streaming: reads only the volume descriptor sectors, the FSD, and each File Entry as it is traversed — all through a `SectorCache`. A 100 GB BD-R UDF image needs only ~256 MB of cache regardless of size.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `UDF Volume` | any | ECMA-167 PVD Volume Identifier (dstring, max 31 ASCII chars). Shown by file managers and the udf driver as the volume label. |

## Storage methods

- `stored` — Stored

## Further reading

- https://ecma-international.org/publications-and-standards/standards/ecma-167/ — ECMA-167 — the base volume/file structure standard
- OSTA "Universal Disk Format Specification, revision 2.01" (osta.org) — the UDF profile of ECMA-167
- https://en.wikipedia.org/wiki/Universal_Disk_Format — Wikipedia article

