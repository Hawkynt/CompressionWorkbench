# VDFS (`Vdfs`)

Gothic game engine VDFS archive (documented by REGoth wiki, Gothic Modding Community, and VdfsSharp)

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.vdf` |
| Recognised extensions | `.vdf` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `50 53 56 44 53 43 5F 56 32 2E 30 30` | 0 | 0.95 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | yes | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | yes | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `VdfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### VdfsFormatDescriptor

Descriptor for Gothic-engine VDFS archives (magic "PSVDSC_V2.00", .vdf) — the virtual-disk container used by Piranha Bytes' ZenGin games. References:

## Storage methods

- `stored` — Stored

## Further reading

- https://gothic-modding-community.github.io/gmc/ — Gothic Modding Community documentation
- https://github.com/REGoth-project/REGoth — REGoth engine reimplementation — includes a VDFS reader
- VdfsSharp — C# VDFS extractor/creator (GitHub)

