# BcacheFS (`BcacheFs`)

BcacheFS Linux filesystem image — R/W (WORM, SB-validated only — fsck parity pending).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.bcachefs` |
| Recognised extensions | `.bcachefs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `C6 85 73 F6 66 CE 90 A9 D9 6A 60 CF 80 3D F7 EF` | 4120 | 0.85 |

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

By moving what is out of place, through `BcacheFsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### BcacheFsFormatDescriptor

Descriptor for BcacheFS volume images (modern Linux FS, mainlined in kernel 6.7). Surfaces the parsed `bch_sb` superblock at offset 4096 as structured metadata plus the raw image, and emits a WORM-minimal, SB-only image via `BcacheFsWriter` that `bcachefs show-super` accepts. Walking the b-tree object graph (extents/dirents/inodes) and emitting B-tree nodes are explicitly out of scope — see `Hawkynt.FileFormats.FileSystems/README.md` for the full gap statement. References:

### BcacheFsReader

Reads the files a `BcacheFsWriter` placed in the CWB-BCH-WB payload area of a bcachefs image.

bcachefs keeps inodes, dirents and extents in b-trees whose keys are varint-packed bkeys; this reader does not walk them. It reads the marker in the reserved sectors ahead of the superblock layout and follows the chained directory the workbench writer left there. An image from real bcachefs format carries no marker and surfaces no entries.

### BcacheFsWriter

Writes a WORM-minimal BcacheFS image: a spec-compliant primary superblock at byte offset 4096 (sector 8), the canonical four-copy `bch_sb_layout` describing the backup superblock locations, and three SB sections: `BCH_SB_FIELD_members_v1` (single device), `BCH_SB_FIELD_replicas_v0` (btree+journal on dev[0]), and a header-only `BCH_SB_FIELD_errors`. The image is sized so every backup-superblock slot named in the layout actually fits inside the file (`MinImageSize` = 128 MiB by default — required because `BCH_MIN_NR_NBUCKETS` = 512 paired with our 256 KiB bucket size needs at least 128 MiB).

Spec source: fs/bcachefs/bcachefs_format.h (kernel) and libbcachefs/sb-members_format.h (bcachefs-tools). Field offsets follow the actual struct layout, NOT the looser interpretation an earlier revision of the read-only descriptor was using:

Scope: this writer satisfies bcachefs show-super on the resulting image. It does not produce a volume a kernel will mount, and does not pretend to: the journal is absent, and with it every btree root, so a mount stops at insufficient_journal_devices during replay. Also missing are the btrees themselves and the clean/journal_v2/counters/ members_v2 SB sections. Reaching a mountable volume is multi-week kernel-spec work tracked in Hawkynt.FileFormats.FileSystems/README.md.

What the superblock does now say is that the volume is initialised. That is not cosmetic: a kernel reads a volume that does not claim it as a device it has been told to make a filesystem on, and makes one — over the top of everything written here, before reporting any error. Claiming it, together with the features every volume carries and the version floor an initialised volume is held to, sends the mount down the recovery path instead, where the volume is refused for what it is missing and left exactly as it was found.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `128 MB`, `256 MB`, `512 MB` | Total image capacity. Must be at least 128 MB so all four backup superblocks fit. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 31 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://bcachefs.org — official site, incl. the "Principles of Operation" on-disk documentation
- https://github.com/koverstreet/bcachefs — canonical source tree (Kent Overstreet); bcachefs_format.h defines bch_sb
- https://en.wikipedia.org/wiki/Bcachefs — Wikipedia overview

