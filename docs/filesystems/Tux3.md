# TUX3 (`Tux3`)

TUX3 version-tree research filesystem (linux-tux3) — single-version WORM image.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.tux3` |
| Recognised extensions | `.tux3` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `54 55 58 33 53 55 50 52` | 4096 | 0.90 |

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

### Tux3Reader

Detection / metadata-surface reader for TUX3 — Daniel Phillips's successor to TUX2, a version-tree based filesystem with copy-on-write metadata and atomic commit semantics. The Tux3 prototype lives in the linux-tux3 tree on kernel.org and uses a superblock magic of "TUX3SUPR" (8 ASCII bytes). Full B-tree traversal of itable / atable is multi-week work; this reader surfaces the parsed superblock as structured metadata plus the raw image. Superblock layout (the documented prefix; little-endian; sits at file offset 4096 == one 4KiB block): 0x00 8 bytes Magic = "TUX3SUPR" 0x08 u64 birthday 0x10 u64 flags 0x18 u64 iroot (root of itable B-tree) 0x20 u64 oroot (root of otable B-tree) 0x28 u64 aroot (root of atable B-tree) 0x30 u64 blockbits 0x38 u64 volblocks 0x40 u64 freeblocks 0x48 u64 nextalloc 0x50 u32 atomgen 0x54 u32 freeatom ...

On top of the documented superblock surface, this reader also recognises an optional WORM file table emitted by `Tux3Writer`: a sentinel header "TUX3WORM" placed at offset 8192 (block 2, immediately after the superblock block) followed by a u32 file count and per-file records (u16 name length, UTF-8 name, u32 data length, raw bytes). Single-version WORM images created by `Tux3Writer` round-trip through this reader; B-tree-formatted prototype images continue to surface only as FULL.tux3 + metadata.ini + superblock.bin.

### Tux3Writer

WORM writer for the TUX3 prototype on-disk surface that `Tux3Reader` parses. TUX3 was Daniel Phillips's version-tree successor to TUX2; the linux-tux3 prototype was never declared stable, so this writer emits the documented superblock prefix (magic "TUX3SUPR" at block offset 4096 plus the documented 0x60-byte field set) followed by a sentinel WORM file table at block 2 (offset 8192). The version-tree itself is collapsed to a single version — no version chain, no atomic-commit log — matching the goal "WORM emit single-version image with N files".

Layout produced (little-endian):

`0x0000 zeroed boot region (4096 bytes, block 0) 0x1000 TUX3 superblock: +0x00 8 bytes Magic = "TUX3SUPR" +0x08 u64 birthday +0x10 u64 flags (0) +0x18 u64 iroot (0 — no B-tree) +0x20 u64 oroot (0) +0x28 u64 aroot (0) +0x30 u64 blockbits (12 — 4096-byte blocks) +0x38 u64 volblocks (image size / 4096) +0x40 u64 freeblocks (volblocks − reserved) +0x48 u64 nextalloc +0x50 u32 atomgen +0x54 u32 freeatom ...zero-padded to end of block... 0x2000 WORM file table (block 2): +0x00 8 bytes Sentinel "TUX3WORM" +0x08 u32 file_count +0x0C ... per-file records: u16 name_len name (UTF-8, name_len bytes) u32 data_len data (data_len bytes)`

Round-trips through `Tux3Reader`. Real linux-tux3 prototype dumps that use the itable/otable/atable B-trees are not emitted by this writer (the B-tree code paths in the prototype were never stabilised); a real-world dump would need a full B-tree writer.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Birthday` | String | `5455583342534831` | any | 64-bit creation stamp written to the superblock at offset 0x08 (hexadecimal). |

## Storage methods

- `stored` — Stored

## Further reading

The implementation cites no sources. Adding a `<list type="bullet">` of them
to the descriptor's doc comment will bring them through to here.

