# OrangeFS / PVFS2 DBPF (`OrangeFs`)

OrangeFS / PVFS2 DBPF storage object — opaque object payload R/W; cluster namespace resolution requires fs.conf.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.orangefs` |
| Recognised extensions | `.orangefs`, `.pvfs`, `.bstream` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `50 56 46 53` | 0 | 0.90 |
| `4F 47 46 50` | 0 | 0.90 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
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

### OrangeFsFormatDescriptor

OrangeFS / PVFS2 DBPF storage-object descriptor. A DBPF file is one server-side storage object rather than a complete distributed filesystem namespace; the opaque object payload can nevertheless be created, replaced and removed while preserving its DBPF tag/version/datastream identity. References:

### OrangeFsReader

Reads OrangeFS / PVFS2 DBPF storage-object files. PVFS2 (the parallel virtual filesystem, now OrangeFS) is a distributed parallel FS, but its server-side storage objects are persisted in single files named like `bstream-XX` using the Direct Block Pool Format (DBPF). Each such file has a 16-byte header with a 4-byte ASCII tag at offset 0: `"PVFS"` (0x50 0x56 0x46 0x53) for classic PVFS2 or `"OGFP"` (0x4F 0x47 0x46 0x50) for OrangeFS-native objects, followed by a version field and a datastream type byte. DBPF header layout (file offset 0, little-endian): 0x00 char[4] tag "PVFS" or "OGFP" 0x04 u32 version (DBPF format revision) 0x08 u32 datastream-type (bytestream / metadata / dirdata / ...) 0x0C u32 object-size (length of contained object payload) 0x10 ... object data The contained object is surfaced as a single opaque entry — full PVFS2 object semantics (handle/fsid resolution + striping) require the cluster's config (fs.conf) and are out of scope.

### OrangeFsWriter

Writes and edits standalone OrangeFS/PVFS2 DBPF storage objects.

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/waltligon/orangefs — official PVFS/OrangeFS repository (DBPF storage layer)
- https://www.kernel.org/doc/html/latest/filesystems/orangefs.html — Linux kernel client documentation

