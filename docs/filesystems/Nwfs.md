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

Read-only descriptor for NWFS386 (Novell NetWare 386 / "Traditional NetWare File System") — used in NetWare 2.x/3.x/4.x and as the SYS: filesystem in 5.x/6.x. NSS (Novell Storage Services) replaced it for new volumes from 1998 but NWFS images still surface in archaeology / migration workflows. **HONEST DISCLAIMER**: this is best-effort detection from public reverse-engineering (notably the zhmu/nwfs project). The on-disk format was never released by Novell. We can identify NWFS partitions by the HOTFIX/MIRROR/Volume signatures but cannot validate contents — directory entries, FAT, suballocation, Turbo FAT etc. are out of scope without an authoritative spec. Magic: `HOTFIX00` — 8 ASCII bytes at byte offset `0x4000` (16384, = sector 32 at 512 B sectors). Confidence 0.85: 8 bytes of ASCII at a fixed offset is high-signal, but because the layout is RE-derived we keep a small margin below the 0.9-0.95 used for spec-stable filesystems. "MIRROR00" and "NetWare Volumes" are detected as corroboration but not used for primary signature matching. References:

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/zhmu/nwfs — primary reverse-engineering project, incl. doc/nwfs386.md
- https://github.com/jeffmerkey/netware-file-system — secondary reference

