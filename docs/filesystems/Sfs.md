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

### SfsFormatDescriptor

Read-only descriptor for Amiga Smart Filesystem (SFS) volume images. SFS is the OFS/FFS replacement used by AmigaOS 4 and AROS, with the complete spec at http://www.xs4all.nl/~hjohn/SFS/ (Amiga SFS spec). Surfaces the parsed root block as a structured metadata bundle; per-file enumeration would require walking the object-container B+ tree. References:

Scope: `CanList` + `CanExtract` + `CanTest` only. The descriptor deliberately does NOT implement IArchiveModifiable or IArchiveCreatable.

Why no R/W: SFS is not a flat-directory filesystem. A real implementation requires:

All four structures cross-reference each other and are checksummed; partial writes corrupt the volume. There is no Linux/Windows-side fsck-class validator (SFS is AmigaOS 4 / AROS only), so an empty-WORM writer would emit bytes nothing can prove correct. Per the project rule (MEMORY.md: "never advertise CanCreate without real spec compliance"), R-only is the honest state for this format.

Promote-to-R/W deferral: a promotion attempt would have to first implement a WORM writer (currently absent — SFS has no SfsWriter companion to the `SfsRootBlock` reader) and then layer in-place B+ tree mutation on top. Both steps require the four cross-checksummed structures above, and the lack of any platform-side validator means the only honesty check would be self-round-trip, which (per the project's WSL-tool-gating rule for filesystems) is insufficient to prove on-disk correctness. The companion R/W promotion of FileSystem.ApplePascal ships in the same session — that format's 26-byte fixed entries and lack of free-space bookkeeping make in-place mutation tractable; SFS does not share either property.

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/aros-development-team/AROS/tree/master/rom/filesys/SFS — AROS SFS implementation — maintained open source
- John Hendrikx's original SFS specification (the xs4all.nl page cited above; now web-archived)
- https://en.wikipedia.org/wiki/Smart_File_System — Wikipedia article
- Object-container B+ tree — keyed by object number, branch/leaf nodes with 24-byte headers, used to map every inode/file/dir to its on-disk extent list.
- Bitmap chain — multi-block free-space map spanning the volume, with checksum each block.
- Directory hash table — Amiga-style FNV-flavoured hash buckets per directory inode, distinct from the object-container tree.
- Free-extent tree — separate B+ tree of coalesced free runs, updated transactionally on every alloc.

