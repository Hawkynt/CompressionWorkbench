# Amiga SFS (`Sfs`)

Amiga Smart Filesystem — root block surface only. R/W deferred: requires writer + object-container B+ tree + bitmap chain + directory hash table + free-extent tree.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.sfs` |
| Recognised extensions | `.sfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `53 46 53 00` | 0 | 0.95 |

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

The implementation carries no description of the on-disk structures. Adding
one to the reader's doc comment will bring it through to here.

## Storage methods

- `stored` — Stored

## Further reading

The implementation cites no sources. Adding a `<list type="bullet">` of them
to the descriptor's doc comment will bring them through to here.

