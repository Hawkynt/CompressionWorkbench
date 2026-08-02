# VxFS (Veritas) (`VxFs`)

VxFS (Veritas File System) image — header-surface read-only.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.vxfs` |
| Recognised extensions | `.vxfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `F5 FC 01 A5` | 1024 | 0.90 |
| `A5 01 FC F5` | 1024 | 0.90 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | no | write a fresh volume holding the given files |
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

### VxFsFormatDescriptor

Read-only descriptor for VxFS (Veritas File System), used by HP-UX, Solaris, and AIX (and a Linux read-only port). Walking the OLT (Object Location Table) → FSH (FileSet Header) → IAU (Inode Allocation Unit) chain to extract user files is explicitly out of scope (multi-week effort) — this descriptor surfaces: Detection: 4-byte magic `0xA501FCF5` at offset 1024. The magic is stored in the natural endianness of the host that wrote the volume — little-endian on x86 / Linux, big-endian on HP-UX PA-RISC and Solaris SPARC. Both signature variants are registered. Create / Modify / Defragment: `NotSupportedException` — the descriptor is read-only. References:

### VxFsReader

Parses the VxFS (Veritas File System / VERITAS / Symantec / now part of Veritas Storage Foundation) on-disk superblock. The structure layout below tracks `fs/freevxfs/vxfs.h` in the Linux kernel (Christoph Hellwig's read-only freevxfs driver) and HP-UX VxFS documentation. Layout summary: Superblock leading fields (per `vxfs.h`): `struct vxfs_sb { uint32_t vs_magic; // offset 0: 0xA501FCF5 int32_t vs_version; // offset 4: VxFS version (1..10 documented) uint32_t vs_mtime; // offset 8: last modification time (Unix time) uint32_t vs_ctime; // offset 12: creation time int32_t vs_old_logstart; int32_t vs_old_logend; int32_t vs_bsize; // offset 24: block size (512, 1024, 2048, 4096, 8192) int32_t vs_size; // offset 28: filesystem size in blocks int32_t vs_dsize; // offset 32: data zone size in blocks uint32_t vs_old_ninode; int32_t vs_old_nau; // offset 40: number of allocation units (old IAU) int32_t vs_old_defiextsize; int32_t vs_old_ilbsize; int32_t vs_immedlen; // offset 52: max immediate-data length (typically 96) int32_t vs_ndaddr; // offset 56: number of direct addresses (10) int32_t vs_firstau; // offset 60: first allocation-unit block ... };` References:

## Storage methods

- `stored` — Stored

## Further reading

- FULL.vxfs — the raw image bytes
- metadata.ini — parsed superblock fields
- superblock.bin — 1 KB capture of the on-disk superblock
- Linux kernel fs/freevxfs/vxfs.h + vxfs_super.c
- HP-UX "VxFS Administrator's Guide" (Veritas / Symantec)
- Wikipedia "Veritas File System"

