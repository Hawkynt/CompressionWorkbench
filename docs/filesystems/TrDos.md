# TR-DOS (`TrDos`)

ZX Spectrum TR-DOS disk image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.trd` |
| Recognised extensions | `.trd` |

## Detection

No byte signature: this format is recognised by its extension and by the
reader accepting the volume's own structures.

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
| move blocks | yes | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `TrDosBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### TrDosFormatDescriptor

Descriptor for ZX Spectrum TR-DOS (Beta Disk interface) .trd disk images — fixed 640 KB geometry with the catalogue and disk-info sector in track 0. References:

### TrDosReader

Reads TR-DOS (.TRD) ZX Spectrum disk images. Enumerates files from the directory at track 0, sectors 0-7. Supports extraction of individual files.

### TrDosWriter

Creates TR-DOS (.TRD) ZX Spectrum disk images.

### TrDosExtentMap

Walks a ZX Spectrum TR-DOS (.trd) disk image (640 KB DSDD: 160 tracks, 16 sectors/track, 256 bytes/sector) and yields the actual on-disk byte layout — the 8-sector directory at track 0 sectors 0..7 plus the disk-info sector at 0x800 as `MetadataReserved`, every per-file contiguous-sector run as a `Used` extent, and the rest as `Free`.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 8 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://sinclair.wiki.zxnet.co.uk/wiki/TR-DOS_filesystem — Sinclair FAQ wiki — TR-DOS filesystem layout
- https://en.wikipedia.org/wiki/TR-DOS — Wikipedia article
- Technology Research "Beta Disk Interface" manual (vendor documentation)

