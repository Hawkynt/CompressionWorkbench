# MSA (Magic Shadow Archiver) (`Msa`)

Atari ST Magic Shadow Archiver disk image with RLE compression

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.msa` |
| Recognised extensions | `.msa` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `0E 0F` | 0 | 0.80 |

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
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### MsaFormatDescriptor

Descriptor for Atari ST MSA (Magic Shadow Archiver) disk images — an RLE-compressed track-image container wrapping a FAT12 floppy filesystem. References:

### MsaReader

Reads Atari ST MSA (Magic Shadow Archiver) disk images. MSA uses simple RLE compression on individual tracks.

### MsaWriter

Creates MSA (Magic Shadow Archiver) disk images from raw ST disk data. Uses RLE compression per track.

## Storage methods

- `rle` — RLE

## Further reading

- https://github.com/hatari/hatari — Hatari emulator; its MSA disk-image support is the de-facto reference implementation
- Magic Shadow Archiver original documentation (Atari ST, Seimet) — no stable online spec

