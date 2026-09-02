# JuiceFS (`JuiceFs`)

JuiceFS — detection-only — distributed POSIX FS with NO standalone on-disk image format: a volume = external metadata DB (Redis/MySQL/TiKV/SQLite/PostgreSQL/etcd/FoundationDB/BadgerDB) + chunks in S3-compatible object storage. R/O is structurally impossible from a single local file because (a) inode→chunk-id resolution lives in the metadata engine and (b) chunk bytes live behind an object-store endpoint. The binary backup's real signature is the BakMagic 0x00747083 (4 bytes BE) in the EOS marker + protobuf footer at end-of-file (juicefs 1.3+); the JSON dump is plain JSON; the SQLite backend uses the standard SQLite header. The offset-0 'JuiceFS' tag is a wrapper convention for surfacing detection only.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.juicefs` |
| Recognised extensions | `.juicefs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4A 75 69 63 65 46 53` | 0 | 0.90 |

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

### JuiceFsFormatDescriptor

Stage 0 detection-only descriptor for JuiceFS artefacts. JuiceFS has no standalone on-disk image format: a volume is the combination of an external metadata engine (Redis / MySQL / TiKV / SQLite / PostgreSQL / etcd / FoundationDB / BadgerDB) plus chunks living in an S3-compatible object store. None of these surfaces are resolvable from a single local file, so R/O extraction is genuinely impossible without those external endpoints; staying Stage 0 is the honest treatment. Surfaces only a synthetic `metadata.ini` and the raw image bytes; no real file-walk is attempted. References:

### JuiceFsReader

Stage 0 detection-only reader for JuiceFS artefacts. JuiceFS is a POSIX-compatible distributed FS with NO standalone on-disk image format: a volume is the combination of an external metadata engine (Redis / MySQL / TiKV / SQLite / PostgreSQL / etcd / FoundationDB / BadgerDB) and chunks living in S3-compatible object storage (S3 / GCS / MinIO / OSS / OBS / …). Real artefacts in the wild: This reader recognises a wrapper-convention tag (ASCII `"JuiceFS"` at offset 0) for surfacing detection only — real JuiceFS files do NOT carry that tag. Even if they did, R/O extraction would still be impossible because (a) inode → chunk-id resolution lives in the metadata engine and (b) chunk bytes live behind an object-store endpoint. Returning empty / zero bytes from `Extract()` would be dishonest; instead we surface the raw image and a self-describing `metadata.ini` that explains why real extraction is structurally impossible.

## Storage methods

- `stored` — Stored

## Further reading

- https://juicefs.com — official JuiceFS site and architecture documentation (metadata engine + object-store chunks)
- https://github.com/juicedata/juicefs — canonical source

