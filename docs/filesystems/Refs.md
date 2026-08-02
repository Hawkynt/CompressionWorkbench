# ReFS (`Refs`)

Microsoft ReFS volume image — boot sector / FSRS header surface only.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.refs` |
| Recognised extensions | `.refs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `52 65 46 53 00 00 00 00` | 3 | 0.85 |

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

### RefsFormatDescriptor

Read-only descriptor for Microsoft ReFS (Resilient File System) volume images. Surfaces the parsed boot sector / FSRS header as a structured metadata bundle plus the raw image. Walking the object table / directory B+trees is explicitly out of scope — that's a multi-week effort and Microsoft's documentation is minimal. Detection alone is the primary win here. References:

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/libyal/libfsrefs — reverse-engineered ReFS documentation + reader (libyal)
- https://learn.microsoft.com/en-us/windows-server/storage/refs/refs-overview — Microsoft's ReFS overview — no on-disk spec is published
- https://en.wikipedia.org/wiki/ReFS — Wikipedia article

