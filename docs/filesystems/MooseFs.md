# MooseFS (`MooseFs`)

MooseFS — partial R/O of master metadata envelope (signature + section index). File content lives on chunk servers and is unreachable from a single metadata image; only metadata.ini + raw image + per-section raw payloads are surfaced.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.mfsm` |
| Recognised extensions | `.mfsm` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4D 46 53 4D` | 0 | 0.90 |

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

### MooseFsFormatDescriptor

Partial R/O descriptor for MooseFS master-metadata images (`metadata.mfs`). Surfaces the metadata envelope (signature, counters, section index) and the raw payload bytes of each walked section. Path-tree (NODE/EDGE) and chunk-id (CHNK) bodies are version-specific and require golden samples to decode honestly — the reader makes no claim about their internal structure.

MooseFS file content lives on chunk servers and is unreachable from a single metadata image. Listing therefore surfaces ONLY synthetic metadata + per-section raw payloads, never POSIX paths.

References:

### MooseFsReader

Partial R/O reader for MooseFS master-metadata images (`metadata.mfs`). MooseFS is a fault-tolerant distributed FS — the master server keeps the namespace + chunk-server topology in a single binary metadata file, while file data lives on chunk servers. This reader understands the master metadata's outer envelope:

The reader walks the section index only — it does not attempt to decode NODE / EDGE record bodies, which differ between MooseFS minor versions and require ground-truth golden samples to validate. NODE/EDGE would give path tree + inode metadata; CHNK gives chunk-id mappings. None of those by themselves yield file content — MooseFS data lives on chunk servers and is only reachable via the live MooseFS protocol. Therefore the reader exposes:

If section-walk fails (signature past the 8-byte tag is not recognised, a section length runs past EOF, the EOF marker is missing, …), the reader falls back to a header-only surface (metadata.ini + raw) and records the parse failure in metadata.ini's parse_status field. This is the honest "we recognise the envelope but couldn't walk the contents" mode rather than silently inventing entries.

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/moosefs/moosefs — canonical source (master metadata dump/load code)
- https://moosefs.com/ — vendor site and documentation
- https://en.wikipedia.org/wiki/Moose_File_System — Wikipedia article

