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

By moving what is out of place, through `ZxSclBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### ZxSclFormatDescriptor

Descriptor for ZX Spectrum SCL archives ("SINCLAIR" signature) — the header+catalogue TR-DOS file container convertible to .trd images. References:

A file's data is found by adding up the lengths of every file before it — the directory records a length in sectors and nothing else, so position is implied by order. That is the whole constraint on moving one: the payloads have to stay packed against the directory and in the order it lists them, and the layout the reader can walk is that one and no other.

Which is what a container we wrote already looks like, because removing a file shifts the payloads back over the gap and truncates. A pass over one of those finds nothing to move and says so, instead of writing the whole container out again to arrive at the same bytes.

### ZxSclReader

Reader for ZX Spectrum `.scl` archives.

Layout:

### ZxSclWriter

Builds a fresh ZX Spectrum `.scl` TR-DOS archive from scratch (WORM).

Layout:

## Storage methods

- `stored` — Stored

## Further reading

- https://sinclair.wiki.zxnet.co.uk/wiki/TR-DOS_filesystem — the TR-DOS catalogue structures the SCL container carries
- https://en.wikipedia.org/wiki/TR-DOS — Wikipedia article — covers the SCL container
- SCL format notes in ZX Spectrum emulator documentation (World of Spectrum formats reference)

