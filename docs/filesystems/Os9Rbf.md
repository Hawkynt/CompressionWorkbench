# Microware OS-9 RBF (`Os9Rbf`)

Microware OS-9 RBF disk image (35-track DSDD CoCo reference, ~315 KB, 256-byte sectors). Files described by file-descriptor sectors with segment lists; root directory only.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.os9` |
| Recognised extensions | `.os9`, `.rbf` |

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

By moving what is out of place, through `Os9RbfBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Os9RbfFormatDescriptor

Read+write descriptor for Microware OS-9 RBF (Random-Block-File) disk images. OS-9 was a multi-tasking real-time OS released in 1979 by Microware Systems; it shipped on the Tandy CoCo, Sharp MZ-2500, embedded systems and later as OS-9/68000 and OS-9000. The writer emits a 35-track DSDD CoCo reference geometry (~315 KB); the reader parses any RBF image whose root directory descriptor is reachable via the identification sector. References:

### Os9RbfReader

Reader for Microware OS-9 RBF (Random-Block-File) disk images. The format was used on the Tandy CoCo (OS-9 Level 1/2), Sharp MZ-2500, Atari MSX-OS-9 machines, and embedded systems running OS-9/68000 and OS-9000. Sector size is 256 bytes; multi-byte fields are big-endian. The directory tree is walked recursively: files in subdirectories surface with their full slash-separated path and each intermediate directory is reported as its own entry.

### Os9RbfWriter

Writer for Microware OS-9 RBF (Random-Block-File) disk images. Emits a 35-track DSDD CoCo geometry (322 560 bytes / ~315 KB, 1260 sectors of 256 bytes, cluster size 1). Files whose names contain '/' separators are placed into real OS-9 subdirectories: each path component becomes a directory file descriptor (directory attribute set) whose data holds "." / ".." links plus one entry per child. Files and directories each occupy one file-descriptor sector and a single contiguous segment.

### Os9RbfExtentMap

Walks a Microware OS-9 RBF disk image (256-byte sectors, big-endian fields) and yields the actual on-disk byte layout — the identification sector + allocation bitmap as `MetadataReserved`, the root directory FD + directory data sectors as `MetadataReserved`, every per-file FD sector as `MetadataReserved`, every (start, count) segment in a file's segment list as a contiguous `Used` extent, and the rest as `Free`.

### Os9Layout

Geometry of the Microware OS-9 RBF (Random-Block-File) reference image used by this writer — a 35-track DSDD 5.25" CoCo floppy: 2 sides × 18 sectors/track × 35 tracks × 256 bytes/sector = 322 560 bytes (~315 KB). Cluster size = 1 sector. Sector numbering is "LSN" (Logical Sector Number); multi-byte fields are big-endian on disk.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 31 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- Microware "OS-9 Technical Reference" (RBF chapter) — the canonical RBF on-disk description
- https://sourceforge.net/projects/nitros9/ — NitrOS-9 — maintained open-source OS-9/6809 with an RBF implementation + ToolShed tooling
- https://en.wikipedia.org/wiki/OS-9 — Wikipedia article

