# NSS (Novell Storage Services) (`Nss`)

NSS (Novell Storage Services) — best-effort anchor detection from publicly available reverse-engineered material; object tree contents cannot be reconstructed. WORM emit deferred: NSS's on-disk format was never publicly documented by Novell (now OpenText). The 'Beast' object record layout, the per-volume B-tree node format, and the trustee ACL tree encoding are not described in any vendor or open-source material we have access to. Emitting a pool that NetWare 5+ / OES would recognise would require a real instance to validate, which we don't have. Pinned at read-only with anchor-detection metadata.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.nss` |
| Recognised extensions | `.nss` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4E 53 53 20 50 6F 6F 6C` | 0 | 0.70 |
| `D4 0A 17 BE 1F 91 13 CC` | 16 | 0.95 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `NssBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### NssFormatDescriptor

Read-only descriptor for NSS (Novell Storage Services) — the pool-based, object-aware filesystem that replaced NWFS386 from NetWare 5+ onwards and remains the default for Novell / OpenText Open Enterprise Server. **HONEST DISCLAIMER**: NSS's on-disk format was never publicly documented by Novell. This descriptor identifies NSS-shaped images by scanning for Novell's embedded ASCII anchors ("NSS Pool", "NSSVolume", "SuperBlk", "Novell", "NetWare") in the first 1 MB of the partition. We can locate the pool descriptor and volume / superblock anchors and surface their byte offsets, but we **cannot** walk the object tree or reconstruct files — the layout (block allocation, "Beast" object records, trustee ACL trees) is proprietary. Magic: `"NSS Pool"` — 8 ASCII bytes detected within the first 1 MB via free-form scan. Confidence 0.70 — distinctive enough to seed a match but lower than well-specified filesystems because (a) the layout is RE'd, not vendor-published; (b) the magic is a brand string that can theoretically appear in non-NSS contexts; (c) we cannot validate the surrounding structure. References:

What this surfaces of a real pool is still only anchors — the pool, superblock and volume headers at the offsets they were found. Nothing here decodes Novell's object store, because nothing public describes it.

What it can do is write a container of its own, carrying those anchors where a real pool carries them and a flat directory behind them, and lay that out again. `NssLayout` says what is in it and why. The two are told apart by a magic behind the pool anchor, so a real pool is detected exactly as it was and refused for anything that would need to know where a file's bytes are.

### NssReader

Best-effort NSS image reader. Parses no object tree — only surfaces the anchors NssHeaders located. Because the on-disk layout is proprietary and lacks a verifiable public spec, we never claim to reconstruct files; we expose the located pool/volume/superblock offsets as synthetic entries the user can correlate with the raw image.

### NssWriter

Writes the NSS container described in `NssLayout`.

What this writes carries its own magic and no NSS anchor. It did carry them once, on the reasoning that an image of ours should be detected by the same scan that detects a real pool — which had it announce itself as an NSS pool while being unable to act as one. Anything that knows NSS would have identified it and then failed to read it, and a format that misleads a reader is worse than one that says nothing.

So the anchors are gone from what is written here. Reading them is untouched: a real pool is still found by them and still surfaced as one whose object tree has no public spec.

### NssLayout

The container this project writes for NSS, and the anchors that make it recognisable as one.

NSS's object tree was never documented by Novell, and the only public structural facts about it are the ASCII anchors its pool, volume and superblock descriptors carry. `NssHeaders` finds those, and that is all anyone here can honestly claim to read of a real NSS image.

So what is written is a container of this project's own shaping, under its own magic and carrying no anchor of a real pool. It carried them once, so that one scan would find both; that had it announce itself as a pool it could not act as, which anything that knows NSS would identify and then fail to read. Saying nothing is better than saying something false. An image from a real NSS pool is still detected exactly as it was — as a pool with anchors and no files this can name.

The layout is deliberately flat. A file is one run of blocks, and its position is a field in the directory rather than anything implied, which is what lets a layout pass move it by rewriting eight bytes.

## Storage methods

- `stored` — Stored

## Further reading

- Novell (OpenText) NSS File System Administration Guide — operational docs only
- https://en.wikipedia.org/wiki/Novell_Storage_Services — pool/volume/object overview
- NetWare 6.5 NSS Storage Management Services documentation

