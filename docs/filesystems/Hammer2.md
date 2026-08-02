# HAMMER2 (DragonFly BSD) (`Hammer2`)

HAMMER2 (DragonFly BSD newer) filesystem image — volume-data sector surface only. WORM emit deferred: HAMMER2 requires four redundant 64 KB volume-data sectors at offsets 0/65536/131072/196608 with consistent generation numbers, a copy-on-write blockref radix tree with per-block xxHash64 checksums across every blockref, per-superroot PFS clusters with their own sub-radix trees, and a real freemap leaf+meta blockmap that survives the COW promotion rules. Multi-week effort, deferred to a future phase.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.hammer2` |
| Recognised extensions | `.hammer2` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `11 20 17 05 32 4D 41 48` | 0 | 0.85 |

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

### Hammer2FormatDescriptor

Read-only descriptor for HAMMER2 (DragonFly BSD newer) filesystem images. Surfaces the volume-data sector at offset 0 plus a structured metadata bundle and the raw image. Walking the HAMMER2 cluster B-tree (radix-tree chains, blockrefs, indirect blocks) is explicitly out of scope (multi-week effort). Magic: 8-byte uint64 at offset 0 = `HAMMER2_VOLUME_ID_HBO` (`0x48414d3205172011`) or `HAMMER2_VOLUME_ID_ABO` (`0x11201705324d4148`). The descriptor's `MagicSignatures` list covers the HBO form (LE serialisation: `11 20 17 05 32 4D 41 48`); the ABO form is recognised by the parser but is rare in practice (only arises when a HAMMER2 image is cross-mounted on opposite-endian hardware). Confidence 0.85: an 8-byte magic at offset 0 is high-confidence but the detector does no secondary sanity check (e.g. volume size plausibility, fstype UUID match). References:

### Hammer2Reader

Walks a HAMMER2 (DragonFly BSD) filesystem image and extracts the regular files living in its PFS roots. The walk mirrors the kernel's on-disk topology (`sys/vfs/hammer2/hammer2_disk.h`):

Directories are recursed so nested files surface under a parent/child path. Data stored with a compression method other than HAMMER2_COMP_NONE (e.g. the kernel's default LZ4) is surfaced raw and flagged via `HasCompressedData` — decompression is out of scope.

### Hammer2Writer

Writes a single-volume HAMMER2 (DragonFly BSD) filesystem image that DragonFly recognises and mounts. The output is a faithful port of what `newfs_hammer2(8)` (`sbin/newfs_hammer2` together with the on-disk helpers in `sbin/hammer2/cmd_setcomp.c` and the kernel's `sys/vfs/hammer2/hammer2_disk.h`) lays down for a fresh, empty filesystem:

The on-disk topology mirrors newfs_hammer2 exactly: the volume header lives in the first of four redundant 64 KB slots (only slot #0 is populated by newfs, the kernel rolls the others forward on the first sync), the boot area starts at 4 MB, the aux area at 12 MB, and the reserved topology area (where the inodes live) begins at 20 MB (allocator_beg). HAMMER2 builds its freemap lazily, so a freshly formatted volume carries no freemap blocks at all — exactly what newfs_hammer2 writes.

Files passed to `AddFile` are materialised in the labelled PFS root exactly the way the DragonFly kernel lays them down: each file gets a regular-file inode (HAMMER2_OBJTYPE_REGFILE) keyed in the root blockset by its inode number, plus a HAMMER2_BREF_TYPE_DIRENT blockref keyed by hammer2_dirhash(name) carrying the filename inline. Payloads up to 512 bytes are embedded directly in the inode's union (HAMMER2_OPFLAG_DIRECTDATA); larger payloads are written to an allocated HAMMER2_BREF_TYPE_DATA block sized to the next power-of-two logical buffer. When the root's four embedded blockrefs overflow they spill into a HAMMER2_BREF_TYPE_INDIRECT block. All data blocks are stored uncompressed (HAMMER2_COMP_NONE) and protected by an xxHash64 check.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Label` | String | `` | any | Labelled PFS name (newfs_hammer2 -L); max 63 ASCII chars. |

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/DragonFlyBSD/DragonFlyBSD/blob/master/sys/vfs/hammer2/hammer2_disk.h
- https://gitweb.dragonflybsd.org/dragonfly.git/blob/HEAD:/sys/vfs/hammer2/DESIGN

