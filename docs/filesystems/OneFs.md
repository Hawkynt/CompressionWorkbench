# Dell EMC Isilon OneFS (`OneFs`)

Dell EMC Isilon OneFS — Stage 0, detection-only — proprietary distributed/clustered FS, no single-image content surface (file data is FEC-striped across nodes). FreeBSD-derived kernel but filesystem layer is NOT UFS-compatible (no UFS1 superblock at 8192). No public on-disk spec; R/O promotion blocked. Magic 'OneFS' / 'ONEF' at offset 0 of LIN-tree root.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.onefs` |
| Recognised extensions | `.onefs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4F 6E 65 46 53` | 0 | 0.90 |
| `4F 4E 45 46` | 0 | 0.85 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | no | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | no | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

It does not.

## How a volume is laid out

### OneFsFormatDescriptor

Stage 0 detection-only descriptor for Dell EMC Isilon OneFS LIN-tree root images. Surfaces only a synthetic `metadata.ini` and the raw image bytes; no real file-walk is attempted.

Why R/O promotion is impossible (per CONTRIBUTING.md promotion gates):

Conclusion: Stage-0 detection only. Surface the magic, raw bytes, and a metadata.ini documenting the limitation. R/O promotion is blocked on (a) Dell EMC publishing the spec and (b) a multi-node ingest path — neither is in reach.

References:

### OneFsReader

Stage 0 detection-only reader for Dell EMC Isilon OneFS LIN-tree root images. OneFS is a clustered scale-out NAS — its single-image surface is the LIN-tree root block, whose first bytes are the ASCII tag `"OneFS"` (5 bytes, 0x4F 0x6E 0x65 0x46 0x53) or the short `"ONEF"` tag (0x4F 0x4E 0x45 0x46 = 0x4F4E4546 BE int) used in some node-local boot images.

Only the tag is verified; the real LIN tree (logical inode number tree) is a cluster-wide construct and cannot be walked from a single image. File data is FEC-striped across nodes (N+M:B protection groups) — even a complete single-drive image carries only one stripe and cannot reconstruct file content without peer nodes.

OneFS shares OS ancestry with FreeBSD, but the on-disk filesystem layer is proprietary and NOT UFS-compatible: there is no UFS1 superblock magic (0x00011954) at the UFS1 superblock offset (8192). The OneFS on-disk format has never been publicly specified by Dell EMC.

## Storage methods

- `stored` — Stored

## Further reading

- No single-image content surface. OneFS is a clustered scale-out NAS — every file is split into "protection groups" striped across drives and nodes with FEC (Forward Error Correction, N+M:B layout, e.g. N+2:1). A single drive/node image carries only one stripe; the file data cannot be reconstructed without the peer nodes. A read-only reader from one image can never return correct file bytes.
- LIN tree is cluster-wide. The Logical Inode Number tree (the OneFS metadata index) lives across nodes, not in a single superblock. There is no per-image inode-to-block mapping to walk.
- Proprietary on-disk format, no public specification. Dell EMC has never published the OneFS on-disk format. No open-source reverse-engineered reader exists. Without a spec we cannot honour the CONTRIBUTING rule "never advertise capabilities you cannot prove against a real spec".
- FreeBSD/UFS ancestry does NOT give us a UFS reader fallback. OneFS runs on a FreeBSD-derived kernel, but the filesystem layer is entirely proprietary — it is NOT FFS/UFS at the on-disk level. UFS1 places its superblock magic 0x00011954 at offset 8192; OneFS images have the ASCII "OneFS" tag at offset 0 and no UFS superblock. Routing OneFS images through UfsReader would fail the magic check and (if forced) return arbitrary bytes — the textbook mutual-compensation trap.
- Dell EMC "PowerScale OneFS Technical Overview" whitepaper — high-level architecture only; no on-disk spec is published
- https://en.wikipedia.org/wiki/OneFS_distributed_file_system — Wikipedia article

