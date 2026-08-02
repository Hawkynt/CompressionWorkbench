# TFS (BBN Trans-FS) (`Tfs`)

BBN Trans-FS transactional filesystem — opaque single-entry surface.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.tfs` |
| Recognised extensions | `.tfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `54 46 53 01` | 0 | 0.80 |

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

### TfsFormatDescriptor

Read-only descriptor for BBN Trans-FS (TFS). TFS is a transactional filesystem developed at BBN; the on-disk format is poorly documented publicly so this descriptor is intentionally detection-only — it emits the raw image as a single opaque entry rather than guessing layout. References:

Magic: 0x54465301 ("TFS\x01") at offset 0. Block size 1024 per the BBN papers. We do not attempt to walk the inode table or directory structure — the published material is insufficient to do that honestly.

## Storage methods

- `stored` — Stored

## Further reading

- BBN Laboratories technical reports on Trans-FS — the only substantive documentation; not stably archived online

