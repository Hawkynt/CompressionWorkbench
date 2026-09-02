# GlusterFS (`GlusterFs`)

GlusterFS — Stage 0 (detection-only, permanent). A GlusterFS brick is a normal directory on a local POSIX filesystem (XFS/ext4); files live at their POSIX paths and metadata lives in xattrs (trusted.gfid, trusted.glusterfs.*). There is no on-disk image format, so no R/O promotion is possible from a single image stream. The 0xCAFE5BAB magic is a workbench-internal probe convention, not a real GlusterFS marker.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.gluster` |
| Recognised extensions | `.gluster` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `CA FE 5B AB` | 0 | 0.90 |

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

### GlusterFsFormatDescriptor

Stage 0 detection-only descriptor for GlusterFS. Honest fallback: GlusterFS has no on-disk image format. A GlusterFS volume is a logical aggregation of one or more "bricks", and every brick is just a normal directory on a local POSIX filesystem (typically XFS or ext4). Volume files live at their normal POSIX paths inside the brick directory and carry GlusterFS state in extended attributes (`trusted.gfid`, `trusted.glusterfs.dht`, `trusted.glusterfs.volume-id`, `trusted.glusterfs.pathinfo`, etc.). There is no superblock, no brick header, no portable single-file representation that this image-based pipeline can consume. We therefore stay Stage 0 permanently. The 0xCAFE5BAB magic recognised here is a workbench-internal convention for hand-dumped brick-object probes — it is not a real on-disk GlusterFS structure and no real GlusterFS deployment will produce it. Promotion to R/O would require walking a live directory tree and reading xattrs, which is outside the image-stream contract enforced by `IArchiveFormatOperations`. References:

### GlusterFsReader

Stage 0 detection-only reader for GlusterFS — permanent honest fallback. GlusterFS itself has no on-disk image format: a brick is a normal directory on a local POSIX filesystem (XFS / ext4 / ...) and volume files are stored at their normal POSIX paths inside that directory. All GlusterFS-specific state lives in extended attributes (the `trusted.gfid`, `trusted.glusterfs.dht`, `trusted.glusterfs.volume-id`, `trusted.glusterfs.pathinfo` namespace). Consequences: The 0xCAFE5BAB magic verified by `Parse` is a workbench-internal probe convention used to dump and round-trip hand-crafted "brick object" experiments; it is not a real on-disk GlusterFS marker and no real GlusterFS deployment produces it. The reader therefore stays a thin two-entry detector (synthetic `metadata.ini` + raw `gluster-brick.bin`) and will never grow real semantics.

## Storage methods

- `stored` — Stored

## Further reading

- https://docs.gluster.org — official GlusterFS documentation (brick/xattr architecture)
- https://github.com/gluster/glusterfs — canonical source
- https://en.wikipedia.org/wiki/GlusterFS — Wikipedia overview

