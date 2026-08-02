# HP LIF (Logical Interchange Format) (`Lif`)

HP LIF volume — flat directory at sector 2, files stored contiguously in 256-byte sectors. Common in HP Series 80 / HP-71 / HP-75 / HP-85 disk and tape images.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.lif` |
| Recognised extensions | `.lif` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `80 00` | 0 | 0.40 |

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

By moving what is out of place, through `LifBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### LifFormatDescriptor

Read+write descriptor for HP LIF (Logical Interchange Format) volumes — a flat-directory disk format used by the HP Series 80, HP-71/75/85 personal computers and compatible HP-IL/HP-IB peripherals from the early 1980s. References:

### LifReader

Reader for HP LIF (Logical Interchange Format) volumes — the disk format used by the HP Series 80, HP-71, HP-75, and HP-85 personal computers as well as the HP-IL/HP-IB peripherals from that era. Volumes contain a flat directory of fixed-length files described by 32-byte directory entries.

### LifWriter

Writer for HP LIF volumes — flat-directory layout with the directory at sector 2 (the conventional LIF starting location). Files are stored contiguously starting after the directory; the writer enforces an upper bound of 14 files unless a caller increases the directory size.

### LifExtentMap

Walks an HP LIF (Logical Interchange Format) volume and yields the actual on-disk byte layout — the volume label sector + the directory sectors as `MetadataReserved`, every per-file contiguous 256-byte sector run as a `Used` extent, and unused sectors as `Free`. Files in LIF are always stored contiguously, so each file produces exactly one Used run.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `DefaultFileType` | Enum | `BIN (0xE020)` | `BIN (0xE020)`, `BPGM (0xE204)`, `DATA (0x0001)`, `TEXT (0xE0F0)`, `BAS (0xE0D0)` | HP LIF 16-bit file-type code stored at directory entry offset 10. Determines how HP Series 80/HP-71 routines treat the file. |
| `DirectorySectors` | Integer | `1` | any | Number of 256-byte sectors reserved for the directory. Each sector holds 8 entries (one is the terminator). Default 1 → max 7 files; raise to fit more. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 6 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://www.hp9845.net/9845/projects/hpdir/ — HPDir project; detailed description of the LIF volume/directory layout
- https://github.com/bug400/lifutils — lifutils — maintained tooling for LIF media images
- HP-UX lif(4) manual page — HP's own on-disk LIF description

