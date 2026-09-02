# Lustre (`Lustre`)

Lustre R/O via ldiskfs (ext4) reader delegation. Surfaces the ldiskfs view of one MDT or OST backing store (file walk over the ext4-compatible block layout); Lustre xattrs (LMA, LOV EA striping, FID) are preserved in the raw image but not interpreted. The Lustre logical view (combining MDT inode metadata with file data striped across multiple OSTs) requires live cluster metadata and is out of scope. Legacy 'LUSTRE'/'LUst' object-header dumps still surface as raw bytes + metadata.ini.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.lustre` |
| Recognised extensions | `.lustre`, `.ost`, `.mdt` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4C 55 53 54 52 45` | 0 | 0.90 |
| `4C 55 73 74` | 0 | 0.85 |

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

### LustreFormatDescriptor

R/O descriptor for Lustre MDT/OST images via ldiskfs (ext4-compatible) reader delegation. Surfaces the ldiskfs view of a single MDT or OST backing store — NOT the Lustre logical view (which would require combining MDT inode metadata with file data striped across multiple OSTs, out of scope without live cluster metadata). Detection is extension-routed (.lustre / .ost / .mdt) and the legacy "LUSTRE" / "LUst" object-header magic at offset 0; ext4 superblock magic is deliberately NOT registered here (it would steal detection from generic ext4 images). When opened with an ldiskfs MDT/OST image (recognised by the .ost / .mdt / .lustre extension), `LustreReader` delegates the file walk to `FileSystem.Ext.ExtReader`. References:

### LustreReader

R/O reader for Lustre MDT/OST images via ldiskfs (ext4-compatible) delegation. Lustre is a high-performance distributed parallel filesystem (originally from CMU, now under OpenSFS). Files are striped across many OST (Object Storage Target) servers and the MDS (MetaData Server) holds the namespace. Lustre's on-disk format for MDT/OST backing stores is `ldiskfs`, a fork of ext4 with Lustre-specific extended attributes (LMA, LOV EA striping, FID pointers) and a few feature flags. The block-level format — superblock, group descriptors, inode table, extent trees, directory blocks — is byte-compatible with ext4 for read purposes. We delegate to `ExtReader` for the file walk and surface the ldiskfs view (the raw inode/directory tree of one MDT or OST), not the Lustre logical view (which requires combining MDT inode metadata with object data striped across multiple OSTs — out-of-scope without live cluster metadata). Detection paths: 1. Legacy "LUSTRE" / "LUst" tag at offset 0 — speculative OST object-header dumps; surfaces metadata.ini + the raw object bytes (Stage-0 behaviour preserved for back-compat). 2. ext4 superblock magic 0xEF53 at offset 1080 — real ldiskfs MDT/OST backing-store image. Surfaces metadata.ini + the ldiskfs file walk via `ExtReader`.

## Storage methods

- `stored` — Stored

## Further reading

- https://www.lustre.org/ — project home
- https://wiki.lustre.org/ — Lustre wiki (architecture, ldiskfs/MDT/OST layout)
- https://en.wikipedia.org/wiki/Lustre_(file_system) — Wikipedia article

