# SmartFS (`SmartFs`)

SmartFS wear-levelled raw-flash filesystem (Apache NuttX). Reads the format sector, walks the root directory and each file's sector chain, and writes a volume in the state mksmartfs leaves behind: logical sector N in physical sector N, sequence numbers at zero, free sectors erased. Wear-level rotation and CRC-protected sector headers are what a running NuttX target adds afterwards; neither is needed to read or lay out a volume.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.smartfs` |
| Recognised extensions | `.smartfs`, `.smart` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `53 4D 52 54` | 10 | 0.85 |
| `53 4D 52 54` | 8 | 0.80 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### SmartFsFormatDescriptor

Read-only descriptor for SmartFS — the wear-levelled raw-flash filesystem in Apache NuttX RTOS. Recognises the "SMRT" format signature near the start of the format sector (NuttX CONFIG_SMARTFS_FORMAT_SIG). Sector-chain traversal + directory enumeration are out of scope; this descriptor surfaces the parsed format sector as metadata plus the raw image. References:

### SmartFsReader

Detection / metadata-surface reader for SmartFS — the wear-levelled raw-flash filesystem in Apache NuttX RTOS. SmartFS uses a logical- to-physical sector map: sector 0 is the "format sector" carrying the partition signature, sector size, and number of root sectors. File data is stored in chains of sectors with a 5-byte logical header (logical sector number, sequence, CRC). Full chain traversal would require modeling the FAT-like sector mapping table plus directory entry walk. This reader surfaces the parsed format sector and image as metadata. Format sector header (selected, little-endian, at file offset 0): 0x00 5 bytes per-sector header (logical sector / status / crc) — exact layout depends on CONFIG_SMARTFS_NLOGSECS ... 0x0A 4 bytes Format signature = "SMRT" (NuttX CONFIG_SMARTFS_FORMAT_SIG) 0x0E 1 byte format version (typically 1 or 2) 0x0F 1 byte sector size code (0=256, 1=512, 2=1024, 3=2048, 4=4096) 0x10 2 bytes number of root directory sectors 0x12 1 byte reserved 0x13+ ...

### SmartFsWriter

Builds a SmartFS volume: a format sector, a root directory, and a sector chain per file.

The volume this emits is what a freshly formatted flash looks like before wear levelling has moved anything: logical sector N sits in physical sector N, every sector's sequence number is zero, and the free sectors past the last file are erased. That is the state mksmartfs leaves behind plus the files, so NuttX reads it as an ordinary volume.

Names are limited to `MaxNameLength` characters, which is the directory entry's fixed name field — the format has nowhere to put a longer one.

### SmartFsLayout

The on-disk shape of a SmartFS volume, as NuttX lays it out.

SmartFS divides the flash into equal sectors. Every sector opens with a five-byte header naming the logical sector it currently holds, which is what lets the wear-levelling layer move a logical sector to a different physical one without anything above noticing. A freshly formatted volume — which is what this writer emits — maps logical to physical one to one.

Past that header, a sector that carries a chain (a directory or a file) opens with a five-byte chain header: the next logical sector, how many bytes of this one are used, and what kind of chain it is. A directory's payload is a run of fixed-size entries; a file's payload is its bytes.

References: fs/smartfs/smartfs.h and drivers/mtd/smart.c in Apache NuttX.

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/apache/nuttx/tree/master/fs/smartfs — reference implementation (Apache NuttX)
- Apache NuttX "SmartFS" documentation and SmartFS Design Document (NuttX project wiki)

