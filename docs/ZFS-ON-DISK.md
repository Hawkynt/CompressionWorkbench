# ZFS — on-disk notes for the reader

`FileSystem.Zfs.ZfsReader` is written from this page, `FileSystem.Zfs.ZfsWriter` from its "Writing a pool" section and `FileSystem.Zfs.ZfsPoolEditor` from "Editing a pool". The normative base is Sun's
*ZFS On-Disk Specification* (2006 draft); it predates pool version 5000, so the
structures added since — feature flags, embedded block pointers, LZ4/zstd, system
attributes, large dnodes, RAID-Z column mapping — are written down here from the
OpenZFS 2.2 headers and sources. OpenZFS is CDDL, which this LGPL repository cannot
take as code, so it was read for behaviour and turned into the text below (sourcing
ladder rung 2). Constants are copied exactly; nothing else is.

Every statement is checked by `Compression.Tests/Zfs/ZfsReferencePoolTests.cs`
against pools that OpenZFS 2.2.2 itself created (`Compression.Tests/Zfs/ReferenceVectors`),
whose files are compared byte for byte with the SHA-256 sums taken from the mounted
pool.

All integers are little-endian on the pools this reader accepts. A pool whose
uberblock magic reads byte-swapped was written on a big-endian host; it is refused,
not guessed at.

## Devices and labels

A leaf device carries four 256 KiB labels: L0 at 0, L1 at 256 KiB, L2 and L3 in the
last 512 KiB of the device size rounded down to 256 KiB. The pool's blocks start
after the first 4 MiB (`VDEV_LABEL_START_SIZE` = two labels + 3.5 MiB boot area).

| Label offset | Size | Content |
| --- | --- | --- |
| 0 | 8 KiB | blank |
| 8 KiB | 8 KiB | boot header |
| 16 KiB | 112 KiB | XDR nvlist (`vdev_phys_t`, trailing 40-byte `zio_eck_t`) |
| 128 KiB | 128 KiB | uberblock ring |

The nvlist is the XDR encoding of libnvpair: a 4-byte header (encoding 1 = XDR,
endian byte, two reserved), then version and flags as big-endian `u32`, then pairs.
Each pair is `encoded_size`, `decoded_size`, name length, name padded to 4, type,
element count, value; a zero `encoded_size` ends the list, and `encoded_size` always
lets an unknown type be skipped. Types used: 1 boolean (no value), 8 `u64`, 9
string, 19 nvlist (version + flags + pairs + terminator), 20 nvlist array (that many
nested lists in a row), 11 `u64` array.

Keys the reader uses: `name`, `pool_guid`, `txg`, `version`, `guid`, `top_guid`,
`vdev_children`, and `vdev_tree` — the device's top-level vdev: `type` (`disk`,
`file`, `mirror`, `raidz`, `replacing`, `spare`, `draid`), `id`, `guid`, `ashift`,
`nparity`, `children` (nvlist array). `id` is the number a DVA names.

### Uberblocks

The ring holds slots of `1 << min(max(ashift, 10), 13)` bytes. A slot is valid when
its magic is `0x00BAB10C` and its embedded checksum matches: SHA-256 over the whole
slot with the last 32 bytes (the checksum words of the trailing `zio_eck_t`, magic
`0x0210DA7AB10C7A11`) replaced by the verifier `{physical offset of the slot in the
device, 0, 0, 0}`. The pool's state is the valid slot with the highest `txg`, ties
broken by `timestamp`; when its tree cannot be read the next one down is tried.

| Offset | Field |
| --- | --- |
| 0x00 | magic |
| 0x08 | version (5000 = feature flags) |
| 0x10 | txg |
| 0x18 | guid_sum |
| 0x20 | timestamp |
| 0x28 | root block pointer (MOS) |

### Digests as stored

SHA-256 is stored as four `u64` words, each the big-endian reading of eight digest
bytes. Fletcher-4 sums the data as `u32` words into four `u64` accumulators
(`a += w; b += a; c += b; d += c`); Fletcher-2 sums it as `u64` pairs
(`a0 += w0; a1 += w1; b0 += a0; b1 += a1`) and stores `a0, a1, b0, b1`. The
`sha512` property is SHA-512/256, stored as the digest bytes in order (no word swap). Skein, Edon-R and BLAKE3 are keyed
with the pool's checksum salt; the reader does not verify them and says so.

## Block pointers (`blkptr_t`, 128 bytes)

| Offset | Field |
| --- | --- |
| 0x00, 0x10, 0x20 | DVA 0..2 |
| 0x30 | properties |
| 0x38 | padding (2 words) |
| 0x48 | physical birth txg |
| 0x50 | logical birth txg |
| 0x58 | fill count |
| 0x60 | checksum (4 words) |

A DVA's first word holds `asize` (bits 0-23, 512-byte units), GRID (24-31) and the
top-level vdev id (32-63); the second the offset (bits 0-62, 512-byte units, after
the 4 MiB start) and the gang flag (bit 63). Copies are alternatives: when one fails
its checksum or cannot be reached, the next is read.

Properties: logical size − 1 (bits 0-15, 512-byte units), physical size − 1 (16-31),
compression (32-38), embedded (39), checksum (40-47), object type (48-55), level
(56-60), encrypted (61), dedup (62), byte order (63, 1 = little-endian).

A pointer that is not embedded and whose DVA 0 is all zero is a **hole**: its
logical extent reads as zeros.

### Embedded pointers

When bit 39 is set the pointer carries its data. The properties become: logical
size − 1 in bytes (bits 0-24), physical size − 1 in bytes (25-31), compression
(32-38), embedded type (40-47, 0 = data; anything else is refused), object type,
level, byte order as before. The payload is the raw bytes of words 0-5, 7-9 and
11-15 in that order (112 bytes at most): everything except the properties word and
the logical birth.

### Gang blocks

A DVA with the gang flag points at a 512-byte gang header: three block pointers
followed by filler and a `zio_eck_t`. The block's physical data is the
concatenation of what the non-hole child pointers read, recursively, cut to the
parent's physical size; the parent's checksum covers that concatenation.

### Compression

| Id | Algorithm | Physical layout |
| --- | --- | --- |
| 2 | off | as is |
| 3 | LZJB | raw stream, see below |
| 4 | empty | all zeros |
| 5-13 | gzip-1..9 | a zlib (RFC 1950) stream, zero padding after it |
| 14 | ZLE | raw stream, see below |
| 15 | LZ4 | big-endian `u32` stream length, then an LZ4 block |
| 16 | zstd | big-endian `u32` stream length, `u32` version/level, then a zstd frame |

**LZJB** groups eight items behind a copy-map byte read least-significant bit
first. A clear bit is a literal byte. A set bit is two bytes `b0 b1`: the match
length is `(b0 >> 2) + 3` and the distance `((b0 << 8) | b1) & 0x3FF`; the copy
runs byte by byte (it may overlap itself) and stops at the declared output length.
The compressor's constants: 6 length bits, minimum match 3, maximum 66, a
1024-entry hash table indexed by `h = (s0 << 16) + (s1 << 8) + s2; h += h >> 9;
h += h >> 5; h & 1023`, and the last 66 input bytes are always literals.

**ZLE** (zero-length encoding, ZFS parameter n = 64): read a byte `c`; if `c < 64`,
copy the next `c + 1` bytes; otherwise emit `c - 63` zero bytes.

## Dnodes (`dnode_phys_t`, 512 bytes per slot)

| Offset | Field |
| --- | --- |
| 0x00 | type |
| 0x01 | log2 indirect block size |
| 0x02 | levels |
| 0x03 | number of block pointers (1-3) |
| 0x04 | bonus type |
| 0x05, 0x06 | checksum, compression |
| 0x07 | flags (bit 2: spill pointer present) |
| 0x08 | data block size in 512-byte sectors (`u16`) |
| 0x0A | bonus length (`u16`) |
| 0x0C | extra slots (large dnodes: the dnode is `(1 + n) * 512` bytes) |
| 0x10 | max block id |
| 0x18 | used |
| 0x40 | block pointers |

The bonus follows the block pointers (`0x40 + 128 * nblkptr`); a spill pointer, when
flagged, is the last 128 bytes of the dnode. An object set's object *n* is the
dnode at byte `n * 512` of its meta-dnode's data.

Logical block *b* of a dnode with `L` levels and `e = indblkshift − 7` pointers per
indirect block (as a power of two) starts at top pointer `b >> (e * (L − 1))`; at
each level below, index `(b >> (e * level)) & ((1 << e) − 1)` of the indirect
block. Indirect blocks are themselves compressed and checksummed. Blocks past the
max block id, and holes, read as zeros.

### Object sets

`objset_phys_t` holds the meta-dnode at 0 and the object-set type at 0x2C0
(1 = MOS, 2 = ZPL filesystem, 3 = zvol).

## ZAP

The first `u64` of block 0 says which kind.

**Micro ZAP** (`0x8000000000000003`): a 64-byte header, then 64-byte entries:
`u64` value, `u32` collision differentiator, `u16` pad, 50-byte NUL-terminated name.
An entry whose name starts with NUL is free.

**Fat ZAP** (`0x8000000000000001`, magic `0x2F52AB2AB` at 8): every other block of
the object whose first `u64` is `0x8000000000000000` and whose `u32` at 24 is
`0x2AB1EAF` is a leaf. A leaf of `2^s` bytes has a 48-byte header, a hash table of
`2^(s−5)` `u16`s, then 24-byte chunks up to the block end. Chunk type 252 is an
entry: value integer length (1), next (2), name chunk (2), name integer count (2),
value chunk (2), value integer count (2), cd (4), hash (8). Type 251 is an array
chunk: 21 bytes of data and the `u16` next chunk (`0xFFFF` ends the chain). Names
are byte strings counted with their NUL; integers in arrays are **big-endian**.
Reading every leaf's entry chunks yields every entry without consulting the pointer
table.

## DSL

The MOS object directory (object 1) maps `root_dataset` to the pool's root DSL
directory. In its bonus (`dsl_dir_phys_t`): head dataset at 8, child-directory ZAP
at 32. Children whose names start with `$` (`$MOS`, `$FREE`, `$ORIGIN`) or contain
`%` (temporary receive clones) are internal.

In a DSL dataset's bonus (`dsl_dataset_phys_t`): snapshot-name ZAP at 32, and the
object set's block pointer at 128. A dataset whose object-set pointer has the
encrypted bit set uses native encryption; its names and contents are ciphertext and
the reader lists the dataset's mount point without contents.

## ZPL

The master node (object 1 of a ZPL object set) maps `ROOT` to the root directory and,
from ZPL version 5, `SA_ATTRS` to the system-attribute master node. Directory ZAP
values hold the object id in bits 0-47 and the `DT_*` type in bits 60-63.

**Version ≤ 4 znodes** (bonus type 17, `znode_phys_t`): mtime (seconds,
nanoseconds) at 16, mode at 72, size at 80, links at 96. A symlink whose target fits
behind the 264-byte structure in the bonus keeps it there; otherwise the target is
the object's data.

**System attributes** (bonus or spill block of type 44): header magic `0x2F505A`
(`u32`), then `u16` layout info — layout number in bits 0-9, header size in 8-byte
units in bits 10-15 — then one `u16` length per variable-length attribute. The SA
master node's `REGISTRY` ZAP maps attribute names (`ZPL_SIZE`, `ZPL_MODE`,
`ZPL_MTIME`, `ZPL_LINKS`, `ZPL_SYMLINK`, …) to `num | length << 24` (number in bits
0-15, fixed length in bits 24-39, 0 = variable); `LAYOUTS` maps the layout number in
decimal to an array of `u16` attribute numbers. Attributes follow the header in
layout order, each starting 8-byte aligned. When the bonus overflows, the remaining
attributes sit in the spill block under their own header and layout.

## Writing a pool

`FileSystem.Zfs.ZfsWriter` writes what this section describes: one leaf device holding a pool
of version 28 (no feature flags) with one ZPL version 3 filesystem. The layout is the one
`zpool create` leaves on a version-28 pool (`Compression.Tests/Zfs/ReferenceVectors/cwbv28`),
reduced to what pool import requires; which MOS directory entries are mandatory was read off
the OpenZFS 2.2.2 import path (CDDL, read for behaviour only). A written pool is proven by
OpenZFS: `zdb -bcc` must report "No leaks (block sum matches space maps exactly)" and no
checksum error, `zdb -r` must copy every file out, and the kernel module must import it, scrub
it with 0 errors and serve every file (`Compression.Tests/Zfs/ZfsWriterTests.cs`).

**Device.** At least 64 MiB (OpenZFS refuses smaller devices), a multiple of 256 KiB, with
the four labels. `asize` = device − 4 MiB − 512 KiB; metaslabs of `2^24` bytes (the shift grows
until there are at most 200), `count = asize >> shift`. Blocks are allocated front to back
from the 4 MiB start in 512-byte sectors (ashift 9); a block never straddles a metaslab. Every
block is born in txg 4, uncompressed (compression 2), checksummed with Fletcher-4, one DVA,
little-endian.

**Labels.** Each `vdev_phys` is the XDR nvlist `version` 28, `name`, `state` 1 (exported),
`txg`, `pool_guid`, `errata` 0, `hostname`, `top_guid` = `guid` (the device), `vdev_children`
1, `vdev_tree` {`type` "file", `id` 0, `guid`, `path`, `metaslab_array`, `metaslab_shift`,
`ashift` 9, `asize`, `is_log` 0, `create_txg` 4}, `features_for_read` {}, sealed with its
SHA-256 `zio_eck_t` (verifier: the device offset of the `vdev_phys`). One uberblock per label,
in slot `txg mod 128` of the 1 KiB slots: magic, version 28, txg, `guid_sum` = pool guid +
device guid, timestamp, the MOS objset pointer; sealed the same way with the slot's offset.

**Mirrors.** An n-way mirror of equally sized devices is one top-level vdev: every device holds
every block at the same offset, and DVAs name vdev 0 as before. Each device's label differs only
in `guid` (its own); `top_guid` is the mirror's guid and `vdev_tree` is {`type` "mirror", `id`
0, `guid` = mirror guid, the metaslab and size keys as above, `children` [{`type` "file", `id`
i, `guid`, `path`, `create_txg` 4} per device]}; the MOS config nests the same mirror under the
root. The uberblock's `guid_sum` covers every vdev of the tree: pool guid + mirror guid + every
device guid.

**Config object.** Its data blocks are 16 KiB (`SPA_CONFIG_BLOCKSIZE`): the kernel rewrites the
config in whole 16 KiB units whenever it changes (a device path that differs on import is
enough), and a config object with one smaller block stops it.

**Packed nvlists** (labels and the MOS `config` object) are libnvpair's XDR encoding: header
`1, 1, 0, 0`, then for every list `u32` version 0, `u32` flags 1 (unique names), the pairs and
two zero words. A pair is encoded size, decoded size, name (XDR string), type, element count,
value (`u64` big-endian; strings as XDR strings; nested lists inline). The decoded size is
libnvpair's in-memory size: `align8(16 + name length + 1) + align8(value)`, the value being 8
for `u64`, string length + 1, 24 for an nvlist and `n * 8 + n * 24` for an nvlist array.

**MOS objects** (object set type 1; the 1 KiB objset holds only the meta-dnode):

| Object | Type (bonus) | Content |
| --- | --- | --- |
| 1 | object directory | micro ZAP: `root_dataset`, `config`, `sync_bplist`, `free_bpobj`, `deflate` 1, `creation_version` 28 |
| root dir | DSL dir (256 bytes) | head dataset, origin = the `$ORIGIN` snapshot, child map {`$FREE`, `$MOS`, `$ORIGIN`}, props ZAP, flags 1, used = filesystem + MOS bytes (HEAD = filesystem, CHILD = MOS) |
| `$MOS`, `$FREE` dirs | DSL dir | parent = root; `$MOS` used = the MOS's bytes |
| `$ORIGIN` dir | DSL dir | head = origin head dataset, clones ZAP {hex(head dataset): head dataset} |
| origin head | DSL dataset (320 bytes) | dir `$ORIGIN`, prev snapshot = origin snapshot (txg 1), snapshot names {`$ORIGIN`}, deadlist {`0`: empty bpobj}, flags 4, hole objset pointer |
| origin snapshot | DSL dataset | next snapshot = origin head, children 2, empty deadlist, next-clones ZAP {hex(head dataset): head dataset} |
| head dataset | DSL dataset | dir root, prev snapshot = origin snapshot (txg 1), empty snapshot names, deadlist {`0`: empty bpobj}, referenced = compressed = uncompressed = unique = the filesystem's bytes, flags 4, objset pointer to the filesystem |
| `free_bpobj`, `sync_bplist` | bpobj (bpobj header, 48 bytes) | empty |
| `config` | packed nvlist (size, 8 bytes) | the pool config: as the label, but `vdev_tree` is {`type` "root", `id` 0, `guid` = pool guid, `create_txg` 4, `children` [the device]} |
| metaslab array | object array | one `u64` per metaslab: its space map object, 0 for none |
| space maps | space map (header, 24 bytes) | three block pointers, so the bonus area is 64 bytes and the map counts as one without a histogram (zdb's feature-refcount check fails otherwise); one 512-byte block: a debug word (`2 << 62`, sync pass 1, txg) and one-word ALLOC entries (`offset << 16 \| run − 1`, both in sectors from the metaslab start); header: object, length in bytes, allocated bytes |

DSL dir bonus offsets: creation time 0, head dataset 8, parent 16, origin 24, child map 32,
used/compressed/uncompressed 40/48/56, props 80, flags 96, used breakdown 104 (HEAD) and 120
(CHILD), clones 144. DSL dataset: dir 0, prev snapshot 8, its txg 16, next snapshot 24,
snapshot names 32, children 40, creation time 48, creation txg 56, deadlist 64,
referenced/compressed/uncompressed/unique 72–96, fsid guid 104 (56 bits), guid 112, flags 120,
objset pointer 128, next clones 256. Deadlists are ZAPs with a 320-byte deadlist header bonus.

**Filesystem** (object set type 2): object 1 is the master node {`DELETE_QUEUE` 2, `ROOT`,
`VERSION` 3, `casesensitivity` 0, `normalization` 0, `utf8only` 0}, object 2 the unlinked set
(an empty ZAP), then the root directory and the tree. Directories are ZAPs whose values are
the object number with the `DT_*` type in bits 60–63 (4 directory, 8 file): a micro ZAP while
every name has at most 49 bytes and the block stays within 128 KiB, otherwise a fat ZAP of
16 KiB blocks (header with the pointer table embedded in its second half, `zt_shift` 10, then
`2^p` leaves; leaf `i` holds the names whose ZAP hash begins with the `p` bits of `i`). The ZAP
hash is CRC-64 (polynomial `0xC96C5795D7870F42`) of the name seeded with the ZAP's salt,
keeping the top 28 bits; names whose hashes collide are told apart by the collision
differentiator (a micro ZAP entry's `u32` at offset 8). Files hold their data in one block of the size rounded up to 512
bytes, or in 128 KiB blocks under 16 KiB indirect blocks. Every directory and file has a
264-byte `znode_phys_t` bonus: four timestamps, generation, mode 0644/0755, size (a
directory's is its entry count + 2), parent, links (2 + subdirectories for a directory), flags
`0x40800000004` (file) or `0x40800000144` (directory), and a trivial three-entry FUID ACL
(`owner@`, `group@`, `everyone@`) matching the mode. Dnodes carry `DNODE_FLAG_USED_BYTES` with
the bytes of all their blocks; a dnode block's fill is the number of objects in it, an objset
pointer's the number of objects.

### Compressed blocks

A file's data blocks may be stored compressed; the writer's `Compression` option and, when
editing, the filesystem's `compression` property decide how. The property is a `u64` in the DSL
directory's props ZAP (the compression id; 0 inherits from the parent directory, unset means
off). On a version-28 pool the choices are lzjb (also what `on` means there), gzip-1…9 and zle;
lz4 and zstd need feature flags. A block is compressed as a ZFS write compresses it:

- a logical block of only zeros is written as a hole, not as a block;
- the compressed stream must be at most `lsize − lsize / 8` bytes (it saves an eighth),
  and once rounded up to whole `2^ashift` sectors it must still be smaller than `lsize`;
  otherwise the block is stored uncompressed (compression 2);
- the physical block is the stream zero-padded to that rounded size; the block pointer gives
  `lsize` and `psize` separately and the compression id, and the checksum covers the physical
  bytes. gzip-N streams are zlib (RFC 1950) at level N.

Charges follow the block: allocated bytes from `psize` rounded to the sector, compressed bytes
`psize`, uncompressed bytes `lsize` for file data (see "Editing a pool", "Accounting"); the
written filesystem's dataset and directories record those three sums.

## Editing a pool

`FileSystem.Zfs.ZfsPoolEditor` adds and removes files in an existing single-device pool of
version 28 — written by `ZfsWriter` or by OpenZFS — as one transaction group, the way this
section describes. The proof is the same as for writing: after edits on a written pool and on
`ReferenceVectors/cwbv28` (a system-attribute root dataset and a ZPL version 3 child holding
lzjb and gzip blocks, a hard link, a sparse file), `zdb -bcc` finds no leak, `zdb -r` copies the
new files out, and the kernel module imports, scrubs (0 errors) and serves exactly the expected
files; in addition every dataset's and `$MOS`'s recorded space and the user and group
accounting move by exactly what a walk of every block tree moves by
(`Compression.Tests/Zfs/ZfsEditorTests.cs`).

**One transaction group.** The new txg is the active one + 1. The active uberblock slot is
copied with its txg, timestamp and root pointer replaced and is written, resealed, into slot
`txg mod slots` of all four labels — after every other block. The label nvlists stay as they
are; a label's `txg` may lag the uberblock's. Blocks are never overwritten: every changed
level-0 block is written elsewhere together with every indirect block above it, the dnode
blocks holding changed dnodes, the meta-dnode's tree, the object set block, the dataset's
objset pointer and so on up to the MOS object set. An indirect block whose children are all
holes becomes a hole; a dnode block with no allocated dnode is a hole. When a write reaches
past what `nblkptr << (levels − 1) · (indblkshift − 7)` can address, a level is added: a new
top indirect block whose first children are the old top pointers. Space freed by a txg is not
reused within it, so older uberblocks still name intact trees.

**Space maps.** Every metaslab with a space map (the metaslab array lists its object, 0 for
none) holds `u64` entries in its data, `smp_length` bytes of them. On a version-28 pool all are
one-word entries: bit 63 clear, offset in bits 16–62 and run − 1 in bits 0–14 (both in units of
`2^ashift` from the metaslab start), bit 15 set for FREE. A word with bits 63–62 = `10` is a
debug entry (action, sync pass, txg) and carries no space; `11` starts a two-word entry, which
belongs to the `spacemap_v2` feature. Replaying ALLOC and FREE in order gives the metaslab's
allocated space, which equals the header's `smp_alloc`. A map may be rewritten whole
("condensed"): one debug word and an ALLOC run for every allocated range, the map's own new
blocks included, in blocks of the object's block size; bytes past `smp_length` mean nothing. A
metaslab that gets its first map gets a new MOS object (three block pointers, 24-byte header
bonus) and the metaslab array is rewritten. The maps after the txg must record every block
allocated or freed in it — those of the maps themselves and of the MOS included — so the
number of map blocks is settled before their contents are.

**Freeing.** A freed block returns every DVA's `asize` (the DVA's 24-bit field, in 512-byte
units) to its metaslab. Gang blocks, deduplicated blocks (property bit 62) and blocks born at
or before the dataset's previous snapshot txg (which a snapshot still references; they would
go to the deadlist) are left to OpenZFS.

**Accounting.** A block's charge is `used` = the sum of its DVAs' `asize` (a plain vdev has
`deflate` 1), `compressed` = its physical size and `uncompressed` = its physical size for
metadata (level > 0, or any object type except 19, 23, 25, 26 and 49) and its logical size
otherwise. A dataset's blocks add to its referenced, compressed, uncompressed and unique
bytes; the MOS's own blocks (the MOS object set block included) belong to the `$MOS`
directory. A directory charged `Δ` under breakdown `t` adds `Δ` to its used, compressed and
uncompressed bytes and to `used_breakdown[t]`; its parent then gets
`a = max(u + Δ, reserved) − max(u, reserved)` (`u` = the directory's used before) added to its
used bytes, `Δ − a` taken from CHILD_RSRV and `Δ` added to CHILD — and so on up the chain.
Breakdown indices: HEAD 0, SNAP 1, CHILD 2, CHILD_RSRV 3, REFRSRV 4. A dnode's `dn_used` holds
bytes when flag bit 0 is set, sectors otherwise.

**User and group accounting.** An object set block of 2 KiB or more holds two more dnodes, the
user-used object at 0x400 and the group-used object at 0x600 (and a project-used one at 0x800
in 4 KiB blocks with the `project_quota` feature). They are micro ZAPs from the id in lower-case
hex to `Σ (512 + dn_used)` over the objects whose dnode has flag bit 1 (`USERUSED_ACCOUNTED`)
and a znode (17) or system-attribute (44) bonus; the ids are the owner and group in the bonus
(`znode_phys_t` offsets 128 and 136; with system attributes at header + 24 and + 32). A
rewritten object gains flag bit 1; what it contributed before is taken away only if it had the
flag. An entry that reaches 0 is removed.

**ZPL on version 5 (system attributes).** A new object's bonus uses the registered layout
whose attributes are, in this order, `ZPL_MODE`, `ZPL_SIZE`, `ZPL_GEN`, `ZPL_UID`, `ZPL_GID`,
`ZPL_PARENT`, `ZPL_FLAGS`, `ZPL_ATIME`, `ZPL_MTIME`, `ZPL_CTIME`, `ZPL_CRTIME` (16 bytes each),
`ZPL_LINKS`, `ZPL_DACL_COUNT` and the variable-length `ZPL_DACL_ACES`: an 8-byte header (magic
`0x2F505A`, layout number | `1 << 10`, then the ACE length 24), the attributes each padded to
8 bytes, and a bonus length of 320 (the whole bonus area of a 512-byte dnode with one block
pointer). The first seven sit at fixed offsets after the header (mode 0, size 8, generation 16,
uid 24, gid 32, parent 40, flags 48). Removing a name removes its directory entry; a file whose
last link goes is freed with its spill block, its extended-attribute directory (`zp_xattr` /
`ZPL_XATTR`) and an external ACL object (`znode_phys_t` offset 176); a directory goes with
everything beneath it and its parent loses a link.

### Intent log

An object set's `zil_header_t` sits at 0x200: claim txg, replay sequence, the first log block
pointer at 0x210, then claimed block and record sequences and flags. A clean export leaves that
pointer naming an allocated block that was never written. A log block was written when its
embedded checksum holds: for checksum 9 (`zilog2`) the block starts with `zil_chain_t` (pad,
next block pointer, `nused` at 136, then the `zio_eck_t` at 144: magic `0x0210DA7AB10C7A11` and
four checksum words) and Fletcher-4 runs over `nused` rounded up to 4 KiB; for checksum 5
(`zilog`) the `zio_eck_t` is the block's last 40 bytes and Fletcher-2 runs over the whole block.
Either way the checksum words are replaced by the block pointer's checksum (the verifier) while
summing. A written block holds records still to be replayed, so such a dataset is not edited.

## RAID-Z

A RAID-Z vdev of `D` children with `P` parity columns and sector shift `a` maps a
block at offset `o` (after the 4 MiB start) of physical size `s` (rounded up to
`2^a`) as follows, all sizes in sectors:

```
b = o >> a, n = s >> a
first column f = b mod D, child offset base = (b / D) << a
q = n / (D − P), r = n − q (D − P)
bc = r == 0 ? 0 : r + P
columns = q == 0 ? bc : D
column c (0 ≤ c < columns): child (f + c) mod D, at base, plus 2^a when f + c ≥ D;
                            size (c < bc ? q + 1 : q) sectors
```

Columns `0..P−1` are parity, the rest data in order. With `P = 1`, columns 0 and 1
trade places (child and offset) when bit 20 of the block offset is set. The data is
the concatenation of the data columns, cut to `s`. Parity column 0 is the XOR of
the data columns (each zero-padded to its length), which recovers one missing data
column; the reader does not solve with the Q and R syndromes, so it needs every data
column of a RAID-Z2/3 block present.

**Writing RAID-Z.** A block of physical size `s` takes `asize` sectors on the vdev:
`n = ceil(s / 2^a)` data sectors, plus `P · ceil(n / (D − P))` parity sectors, rounded up to a
multiple of `P + 1` (the padding sectors are allocated, never written). The DVA's `asize` and
the space maps carry that size. The writer fills the data columns in order with the block's
bytes (zero-padded to whole sectors) and computes the parity columns, each as long as column 0,
over the data columns in order, a short column counting as zeros: P = XOR of the columns;
Q = `Q·2 ⊕ D` and R = `R·4 ⊕ D` per byte, multiplication in GF(2^8) with polynomial 0x11D
(`x·2` = `x << 1`, XOR 0x1D when the top bit was set). The label's `vdev_tree` is {`type`
"raidz", `id` 0, `guid`, `nparity` P, the metaslab and size keys, `children` [the devices]}; its
`asize` is D × the devices' own `asize`. Datasets are charged `dsize` = `asize` in 512-byte
units × the vdev's deflate ratio `2^17 / (asize of a 128 KiB block in 512-byte units)` (integer
division; 512 on plain and mirror vdevs, where `dsize` = `asize`). Dnodes' `dn_used` counts the
same `dsize`.

Mirror, replacing and spare vdevs hold full copies on every child. dRAID and indirect
(removed) vdevs are refused.
