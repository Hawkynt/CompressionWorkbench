# NWFS (Novell NetWare 386 Traditional Filesystem) (`Nwfs`)

NWFS (Novell NetWare 386 Traditional Filesystem) — best-effort detection from public RE; contents cannot be validated.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.nwfs` |
| Recognised extensions | `.nwfs`, `.nwvol`, `.netware` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `48 4F 54 46 49 58 30 30` | 16384 | 0.85 |
| `48 4F 54 46 49 58 30 30` | 32768 | 0.80 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | no | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | no | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

It does not.

## How a volume is laid out

### NwfsFormatDescriptor

Read-only descriptor for NWFS386 (Novell NetWare 386 / "Traditional NetWare File System") — used in NetWare 2.x/3.x/4.x and as the SYS: filesystem in 5.x/6.x. NSS (Novell Storage Services) replaced it for new volumes from 1998 but NWFS images still surface in archaeology / migration workflows. **PROVENANCE**: Novell never released the on-disk format. What is read and written here follows the public reverse-engineering of it (notably the zhmu/nwfs project, whose documentation and reader were both checked against). Volumes written by `NwfsWriter` are read back by that project's own `transfer` tool — directory tree, sizes and file bytes all agreeing — so contents are no longer merely detected. Still out of scope: suballocation, Turbo FAT, compression, mirrored partitions, volumes spanning several partitions, and the salvage area. A volume using any of those reads only as far as its plain structures go. Magic: `HOTFIX00` — 8 ASCII bytes at byte offset `0x4000` (16384, = sector 32 at 512 B sectors). Confidence 0.85: 8 bytes of ASCII at a fixed offset is high-signal, but because the layout is RE-derived we keep a small margin below the 0.9-0.95 used for spec-stable filesystems. "MIRROR00" and "NetWare Volumes" are detected as corroboration but not used for primary signature matching. References:

### NwfsReader

Reads a NetWare 386 volume: finds the partition, walks the directory chain, and follows a file through the FAT to its bytes.

The route is the one a NetWare reader takes. The partition table gives the partition; the hotfix header at sector 32 of it gives the distance to the volume area; the volume area gives the block size and the block the directory starts at; and the data area, which follows the volume area, is what every block number counts from.

Directory entries are flat. Each names the directory it belongs to rather than being nested inside it, so a path is walked by collecting every entry once and then following parent ids down from the root.

### NwfsWriter

Writes a NetWare 386 disk image: a partition table naming one NetWare partition, the hotfix, mirror and volume headers that open it, and a volume whose files a NetWare reader walks by the same route a real one does.

How a volume is found. A reader takes the partition's start from the partition table, reads the hotfix header at sector 32 of it, and takes from there how many redirection sectors separate that header from the volume area. The volume area names the volume, its block size, and the block its directory begins at. Everything after the volume area is the data area, and block numbers count from its first byte.

How a file is found. The directory is a chain of blocks holding fixed 128-byte entries, each naming the directory it sits in rather than being nested under it — so a reader collects the lot and then filters by parent. A file entry carries its length and its first block; the rest of it is followed through the FAT, which sits at the very start of the data area and gives, for each block, the block that comes after it.

What is written for the sake of being ordinary. A volume carries a volume-information entry ahead of its files, its unused directory slots are marked available rather than left zero — a zeroed slot would read as an unnamed file in the root — and the directory is written twice, the second copy where a real volume keeps its own.

### NwfsLayout

Where the areas of an NWFS386 partition sit, and the few arithmetic rules that tie them together.

A NetWare partition opens with a hotfix header at its own sector 32, the mirror header in the sector after that, and the volume area a stated number of redirection sectors further on. The data area follows the volume area, and every block number a volume uses is counted from there.

The block size is not stored directly. The volume entry carries a divisor instead, and the size is (256 / divisor) * 1024 bytes — so a divisor of 64 means blocks of 4 KB.

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/zhmu/nwfs — primary reverse-engineering project, incl. doc/nwfs386.md
- https://github.com/jeffmerkey/netware-file-system — secondary reference

