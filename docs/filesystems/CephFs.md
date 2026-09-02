# CephFS / RADOS (`CephFs`)

CephFS / RADOS — detection-only — distributed FS, no single-image content surface. Magic 'CEPH' at offset 0 of OSD object metadata. Stage-0 confirmed: metadata lives in a RADOS metadata pool (MDS-managed), file data is striped across many RADOS objects placed via CRUSH across OSDs (BlueStore/FileStore backends); R/O over a single image is structurally impossible without the live mon/mds cluster state.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.ceph` |
| Recognised extensions | `.ceph`, `.rados` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `43 45 50 48` | 0 | 0.90 |

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

### CephFsFormatDescriptor

Stage 0 detection-only descriptor for CephFS / RADOS OSD object metadata dumps. Surfaces only a synthetic `metadata.ini` and the raw image bytes; no real file-walk is attempted.

Stage-0 confirmation — promotion to R/O is structurally impossible from a single image. CephFS has no standalone on-disk image format. A CephFS volume consists of:

Reconstructing a CephFS namespace would require: (a) a full OSD-set snapshot, (b) the live mon/mds cluster state (osd-map, mds-map, CRUSH-map), and (c) a BlueStore reader. Even with all three, the result is OSD-level objects, not CephFS-level paths. Treatment confirmed: stay Stage 0.

References:

### CephFsReader

Stage 0 detection-only reader for CephFS / RADOS OSD object metadata dumps. Ceph is a distributed object store (RADOS) with the CephFS POSIX namespace layered over it via MDS daemons — files become RADOS objects sharded across many OSDs. Single OSD object metadata dumps begin with the ASCII tag `"CEPH"` (0x43 0x45 0x50 0x48 = 0x43455048 BE). Only the tag is verified. Full RADOS semantics (object name → PG mapping via CRUSH, replica/EC erasure coding, MDS namespace resolution) require a live Ceph cluster's mon/mds state.

Stage-0 confirmation (no promotion possible from a single image). A CephFS volume is metadata-in-pool plus data-striped-across-OSDs:

Promotion to R/O would require simultaneous access to a full OSD-set snapshot, the live cluster maps (mon/mds/osd/CRUSH), and a BlueStore reader — and even then the surface is OSD-level objects, not CephFS-level paths. Conclusion: stay Stage 0. The honest deliverable is magic-tag detection + metadata.ini + raw bytes.

## Storage methods

- `stored` — Stored

## Further reading

- Metadata (inodes, dirfrags, MDS journal) stored as RADOS objects inside a dedicated metadata pool, managed by one or more MDS daemons. Resolving a path requires replaying the MDS journal and walking dirfrag objects across the metadata pool.
- File data striped across many RADOS objects (default 4 MiB stripe-unit, named {inode}.{stripe-index}) and placed across OSDs via CRUSH against the cluster's mon-map / osd-map / CRUSH-map — none of which live in any single file.
- OSDs themselves store those RADOS objects in a BlueStore (RocksDB + raw-block) or legacy FileStore backend; neither exposes CephFS-level paths.
- https://docs.ceph.com/en/latest/cephfs/ — official CephFS documentation (MDS, RADOS layout, striping)
- https://github.com/ceph/ceph — canonical Ceph source
- https://en.wikipedia.org/wiki/Ceph_(software) — Wikipedia overview

