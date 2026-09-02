# NIB (Commodore nibble dump) (`Nib`)

Commodore raw nibble dump with raw-track, strict sector, and CBM DOS driver layers

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.nib` |
| Recognised extensions | `.nib` |

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
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### NibFormatDescriptor

Fixed-slot NIB raw nibble dump. Archive-level operations expose track slots; block/filesystem providers expose strict canonical 1541 sector media.

### CbmNibbleReader

Reader for Commodore 1541/1571 nibble dumps — both raw .nib fixed-slot images and VICE .g64 track containers. The pseudo-archive surface is one opaque GCR payload per half-track; callers that need filesystem semantics can explicitly decode those tracks to a D64 through `DecodeToD64`.

### CbmNibbleWriter

Writer for Commodore nibble containers. It supports two distinct layers: ordinary Commodore files are first placed into a D64 and GCR-encoded, while pseudo-archive callers can directly build G64/NIB containers from opaque `track_XX.bin` payloads without touching the filesystem inside them.

## Storage methods

- `stored` — Fixed 8192-byte GCR slots

## Further reading

The implementation cites no sources. Adding a `<list type="bullet">` of them
to the descriptor's doc comment will bring them through to here.

