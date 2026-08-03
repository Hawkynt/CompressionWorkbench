# SCL (ZX Spectrum) (`ZxScl`)

ZX Spectrum SCL archive (TR-DOS compact form)

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.scl` |
| Recognised extensions | `.scl` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `53 49 4E 43 4C 41 49 52` | 0 | 0.95 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | yes | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### ZxSclReader

Reader for ZX Spectrum `.scl` archives.

Layout:

### ZxSclWriter

Builds a fresh ZX Spectrum `.scl` TR-DOS archive from scratch (WORM).

Layout:

## Storage methods

- `stored` — Stored

## Further reading

The implementation cites no sources. Adding a `<list type="bullet">` of them
to the descriptor's doc comment will bring them through to here.

