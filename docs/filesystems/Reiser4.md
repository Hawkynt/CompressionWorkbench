# Reiser4 (`Reiser4`)

Reiser4 filesystem image — master + format40 superblock surface only.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.reiser4` |
| Recognised extensions | `.reiser4` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `52 65 49 73 45 72 34` | 65536 | 0.90 |

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

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### Reiser4FormatDescriptor

Read-only descriptor for Reiser4 filesystem images (successor to ReiserFS 3.6 — completely different on-disk layout). Surfaces the master superblock at offset 65536 and, when present, the format40 superblock that follows it, plus a structured metadata bundle and the raw image. Walking the twig-level B-tree is explicitly out of scope (multi-week effort). Magic: References:

### Reiser4Reader

Reads a Reiser4 image: the master superblock's label, UUID and block size, and the files a `Reiser4Writer` placed in the CWB-R4-WB payload area.

The reserved blocks of a workbench-written image are byte-exact mkfs.reiser4 captures describing an empty storage tree, so there is no reiser4 tree here to walk. Files live past those blocks, announced by a marker in the master superblock's spare region and described by a chained directory — the layout `Reiser4Writer` documents. An image from a real mkfs.reiser4 carries no marker and surfaces no entries; its storage tree (extent40 bodies keyed by file offset, cde40 directory units) is out of scope.

### Reiser4Writer

WORM (write-once-read-many) creator for an **empty** Reiser4 filesystem image that is byte-exact-compatible with what `mkfs.reiser4 -fffy` from `reiser4progs 1.2.2` produces, and that `fsck.reiser4` validates as `"FS is consistent."`.

The image holds 25 reserved blocks at fixed positions (block size = 4 KB):

Implementation strategy: we embed the seven non-zero reference blocks as resources captured byte-exact from a real mkfs.reiser4 image, then patch in only the per-image fields:

Round-trips with fsck.reiser4 -y exit 0 and produces output identical to the reference image except for the random fields above.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `16 MB`, `32 MB`, `64 MB`, `128 MB` | Total image capacity. Auto sizes the image to exactly hold the files (recommended). |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 16 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- "ReIsEr4" at offset 65536 — master superblock ms_magic[16].
- https://archive.kernel.org/oldwiki/reiser4.wiki.kernel.org/ — archived Reiser4 wiki (format40 layout, plugin system)
- reiser4progs (mkfs.reiser4 / debugfs.reiser4) — canonical userspace tooling
- https://en.wikipedia.org/wiki/Reiser4 — Wikipedia article

