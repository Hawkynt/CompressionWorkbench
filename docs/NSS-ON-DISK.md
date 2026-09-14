# NSS (Novell Storage Services) — on-disk notes

What is written here was derived by running Novell's own NSS for Linux and
reading the media it produced. It is **not** a specification. The project still
does not write native NSS volumes: `FileSystem.Nss` emits a separate container
under its own magic that deliberately carries no NSS anchor, so nothing it
produces can be mistaken for a pool.

These notes now serve two purposes: anchor detection and a deliberately narrow,
read-only decoder for quiescent NSS media. The decoder scans 4 KiB metadata
blocks, reconstructs paths from structurally valid `DirH` ZID/parent/name
records, cross-checks ordinary file records in `LEAF` blocks, and exposes only
contiguous extents whose translated block range fits entirely inside the image.
It does not claim to implement the full object tree, journal or write path.

## How the media was obtained

Novell never published the format, and no free implementation exists. The
implementation itself is still distributable: the OES 11 SP2 evaluation media
carries `novell-nss` (`mkfs.nsspool`, `mkfs.nssvol`, `nsscon`, `nssmu`),
`novell-nss-nlvm` (`nlvm`) and `nss-kmp-default` (`nsszlss.ko`, `nss.ko`).

The kernel modules are built for 3.0.76, which is also the kernel of the
installer's own rescue system — so a pool can be created in a VM without
installing anything. `mkfs.nsspool` alone is not enough; a pool it writes is
never discovered. Only pools created through `nlvm` become active.

`nsszlss.ko` is **not stripped** — it exports 2783 named symbols, including
`BeastNode_valid`, `DBT_doInsertEntry` and `DBT_findLeaf`, which is how the
node rules below were confirmed rather than guessed. Those binaries are used
only as behavioral/structural oracles; no proprietary implementation code is
copied or translated.

## Block addressing

Blocks are 4 KiB. Block numbers stored in metadata are **8 less** than the
absolute block in the image:

    absolute = stored + 8

This holds for file extents and for the numbers NSS prints in its own error
messages — "VolumeData Block 458627" is absolute 458635.

## Structures

Every metadata block opens with a four-byte tag, and carries a counter at +8
with the constant `0xC000` at +10. The counter rises monotonically across the
pool as blocks are written, so the highest belongs to the most recently written
block. Tags seen: `LEAF`, `BRCH`, `DirH`, `ZLBH`, `ZVLo`, `ZPLo`, `XLEF`,
`ULEF`, `ZVol`, `ZPoo`, `SPB5`, `CKP7`, `SBD0`, `SBDX`, `PASM`, `PLOG`.

### Directory block — `DirH`

Entries are 64 bytes, starting at +48, with a **sorted index at the tail**: one
u32 per entry holding its offset relative to +48, written upward and ending at
+4092, so the array grows downward. The index is kept in name order.

| offset in entry | meaning |
|---|---|
| +8 | ZID (object id) |
| +24 | parent ZID (`0x7f` is the volume root) |
| +42 | name length, in characters |
| +44 | name, UTF-16LE |

Header: entry count at +6, bytes-used at +44 (64 per entry), free space at +40,
and immediately above the index array a guard word `0x80000000 | (free / 4)`.
An entry therefore costs **68** bytes of free space — 64 for itself and 4 for
its index slot.

### Object tree — `LEAF` / `BRCH`

`LEAF` blocks hold the objects; the u16 at +4 is the node type (`0x03` leaf,
`0x11` index). Records are variable length and live at **`node + 0x28 +
relative`** — the record area starts at +40, and both the tail index and the
`used` field are relative to that. The tail index is u16 per record, ending at
+4094 and growing down.

`BeastNode_valid` enforces: the tag must be `LEAF` or `BRCH`; every index entry
must be ≤ `0xfd8`; each record's u16 at +2 must be `0x444E` (only the first
record walked may be `0x3030`); and record keys must ascend across the walk.

A file's record is 272 bytes:

| offset in record | meaning |
|---|---|
| +8 | ZID |
| +24 | size in bytes |
| +48, +52 | block count |
| +88 | extent block count |
| +92 | extent start block (stored numbering) |
| +140 | name length, in characters |
| +142 | name, UTF-16LE |
| +204… | timestamps |

The name is duplicated here and in `DirH`; the two must agree. The reader treats
that agreement as a required cross-check before exposing a native file.

`ZLBH` blocks hold a **history** of record versions for the same object — a full
decoder must take the newest, not the first. The initial reader does **not** yet
decode `ZLBH`; it only resolves ordinary `LEAF` records and prefers the highest
observed metadata-block counter for duplicate ZIDs.

### Free space — `XLEF`

A flat array of 8-byte pairs `(u32 length, u32 start)` from +56, ascending by
start and zero-terminated. Allocation reduces a length and advances its start.
Free space is not a bitmap.

## Reader profile implemented in CompressionWorkbench

The native reader intentionally stays narrower than the notes above. It:

- requires a seekable image and at least one real NSS anchor;
- scans 4 KiB blocks for structurally valid `DirH` and leaf-type `LEAF` blocks;
- reconstructs nested paths by following ZID → parent-ZID links to root parent
  `0x7f`, rejecting cycles and missing parents;
- derives directories from ZIDs that are referenced as parents;
- exposes a regular file only when its `DirH` and `LEAF` names match exactly;
- accepts only the documented single contiguous extent (`+88` count, `+92`
  stored start) and validates the translated byte range against the image;
- remains read-only and does not interpret `ZLBH`, trustee/ACL trees, sparse or
  multi-extent variants, deleted/salvage state, compression or encryption.

This is intentionally a fail-closed recovery/inspection profile, not a claim of
complete NSS compatibility.

## Working on an image directly

Take the image only after **`nlvm pool deactivate POOL1`**, not merely after
unmounting the volume. A clean deactivation rewrites the `CKP7` checkpoints
(blocks 10 and 266) and `PASM`, leaving nothing outstanding.

This matters: an image copied while the pool was active still has journal
entries to replay, and mounting it replays them — which silently reverts an
edited `DirH` header while leaving the raw entry bytes in place, so the change
appears to have been ignored rather than undone.

## What is not known

Volumes hand-authored from these notes **mount and read correctly**, verified
cold against Novell's own module — a file written with its own allocation reads
back byte-exact, and on a quiesced image it is listed on the first mount. They
are, however, **read-only in practice**: NSS refuses the first write to them and
deactivates the pool.

The cause is not established. Nine explanations have been tested and ruled out,
including the record's contents (NSS's own bytes for a file of identical shape
were transplanted), every header counter, the parent index node (NSS leaves it
untouched when it adds a record itself), and the journal. Journalling is real —
`DBT_logInsertRecord` and its siblings log every tree modification, and replay
is what reverts edits to an unquiesced image — but a quiesced base with a
correctly placed record still refuses writes, so the log is not the explanation
either.

Anything produced from these notes should therefore be treated as read-only
until that is understood.
