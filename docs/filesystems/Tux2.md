# TUX2 (`Tux2`)

TUX2 phase-tree research filesystem (Daniel Phillips, OLS 2002) — single-phase synthetic image.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.tux2` |
| Recognised extensions | `.tux2` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `54 55 58 32 46 53 00 00` | 0 | 0.90 |

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

### Tux2FormatDescriptor

Read+WORM descriptor for TUX2 — Daniel Phillips's 2002 phase-tree filesystem proposal (OLS 2002 paper, never-stabilised research format). Recognises a deterministic header pattern (magic "TUX2FS\0\0" at offset 0) so research images we generate round-trip through the reader. Writer emits a single-phase image only (no alpha/beta phases, no version chain) — real legacy prototype images would need a custom parser matching the specific snapshot of the in-progress code that produced them. References:

### Tux2Reader

Detection-only / synthetic-image reader for TUX2 — Daniel Phillips's 2000-era "phase tree" filesystem proposal. TUX2 was a research design (atomic phase-tree commits, copy-on-write metadata) that never reached a stable on-disk layout shipped to end users. No public spec for the in-progress prototype's on-disk format ever stabilised; the project was eventually superseded by TUX3. Because no canonical TUX2 images exist in the wild, this reader recognises a deterministic synthetic header — a chosen 8-byte ASCII magic "TUX2FS\0\0" at offset 0 followed by a small JSON-ish payload — so that the descriptor at least round-trips its own synthetic images for testing. Real TUX2 prototype dumps (if any survive) would need a custom parser matching the specific cvs-era code path that produced them. Synthetic header layout (little-endian): 0x00 8 bytes Magic = "TUX2FS\0\0" 0x08 u32 version (1) 0x0C u32 file_count 0x10 ... per-file records: u16 name_len name (UTF-8, name_len bytes) u32 data_len data (data_len bytes)

### Tux2Writer

WORM writer for the TUX2 synthetic image layout that `Tux2Reader` parses. TUX2 was a 2002-era phase-tree research filesystem (Daniel Phillips, kernel.org/doc/ols/2002/) whose on-disk format never stabilised — no canonical real-world images exist. The reader documents (and round-trips) a deterministic synthetic header that we emit here: `0x00 8 bytes Magic = "TUX2FS\0\0" 0x08 u32 version (1) 0x0C u32 file_count 0x10 ... per-file records: u16 name_len name (UTF-8, name_len bytes) u32 data_len data (data_len bytes)` Single-phase only (no alpha/beta phases, no version chain) — matches the goal of "WORM emit single-phase image with N files (no research-level snapshots)". Round-trips through `Tux2Reader`.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Version` | Integer | `1` | any | Format version stamped into the TUX2 header at offset 0x08. |

## Storage methods

- `stored` — Stored

## Further reading

- Daniel Phillips, "The Tux2 Filesystem" (Ottawa Linux Symposium 2002 proceedings) — the defining paper
- https://en.wikipedia.org/wiki/Tux3 — Wikipedia article covering the phase-tree lineage

