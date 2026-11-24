# bcachefs on disk — what this package writes

The volume `BcacheFsWriter` writes, and the in-place edits re-publish, is
**metadata version 1.3 (`rebalance_work`)**: the newest version the
bcachefs-tools Debian and Ubuntu ship (1.3.x) can open, and the version their
`bcachefs format` writes. Current tools (v1.39.6) check it after an in-memory
upgrade, and mount it through FUSE. The Linux kernel's own driver (Ubuntu 24.04's
6.8, booted in the libguestfs appliance because WSL's 6.6 has none) mounts it
read-write, shows the tree, modes, owners, times, xattrs, links and bytes that went
in, and writes into it; this package then reads what the kernel wrote and edits it
in place (`Compression.Tests/BcacheFs/BcacheFsKernelMountTests.cs`).

## How this was established

bcachefs and bcachefs-tools are GPL-2.0, which this LGPL repository cannot
absorb, so this is sourcing rung 2: their source was read to write this document,
the code was written from this document, and the tools are the oracle. Struct
layouts, bit positions and constants are copied exactly — matching them is the
specification.

Each fact below is marked:

- **measured** — read off a volume `bcachefs format` (Ubuntu bcachefs-tools,
  reports `1.3.3`; CI installs `1.3.4`) wrote on a 128 MiB device and shut down
  cleanly, by this package's own core reader;
- **read** — taken from the v1.3.4 release source
  (`libbcachefs/bcachefs_format.h`, `alloc_background.[ch]`, `backpointers.h`,
  `btree_gc.c`, `recovery.c`, `sb-clean.c`, `lru.h`, `journal.h`);
- **checked** — a rule whose violation `bcachefs fsck -n` 1.3.x was shown to
  report;
- **kernel** — what the 6.8 kernel (`6.8.0-146-generic`, module `bcachefs.ko`) did
  to a volume this package wrote, seen through its mount and read back here.

The proof is in `Compression.Tests/BcacheFs/BcacheFsExternalConformanceTests.cs`
(fsck on every shape of volume and after every kind of edit, a deliberately wrong
total that fsck must report, a FUSE mount comparing tree and bytes) and
`BcacheFsReferenceVectorTests.cs` (volumes the tools wrote, read back here).

## Why 1.3 and not 1.38

The writer used to stamp 1.38. bcachefs-tools 1.3.x refuses any volume newer
than it knows before reading a byte — `error validating superblock: Filesystem
has incompatible features` — so the distribution's checker could never judge it,
and neither could any kernel older than the version. Anything that knows 1.3
reads a 1.3 volume, and the newer tools upgrade it.

What 1.3 has not got, and this writer therefore does not write: the accounting
btree (1.9; usage lives in the clean section instead), `bucket_gen` and `flags`
in backpointers (1.14), the members' btree-allocated bitmap (1.7), the superblock
`ext` field, `incompat_version_field`, the logged-ops inode cursor.
[BCACHEFS-ACCOUNTING.md](BCACHEFS-ACCOUNTING.md) records how the 1.38 accounting
keys were laid out, for reading newer volumes.

## Superblock

| Field | Value | Source |
|---|---|---|
| `version`, `version_min` | `0x0403` (1.3) both | measured |
| `block_size` | 1 sector (the tool picks the device's; any power of two ≥ 512 is valid) | read |
| `time_base_lo`, `time_precision` | 0 and 100 ns (the tool: format time and 1 ns; any precision up to a second is valid) | read |
| `features[0]` | `new_siphash`, `new_extent_overwrite`, `btree_ptr_v2`, `new_varint`, `journal_no_flush`, `alloc_v2`, `extents_across_btree_nodes` = `0x78A80` | measured |
| `compat[0]` | `alloc_info`, `alloc_metadata`, `extents_above_btree_updates_done`, `bformat_overflow_done` = `0xF` | measured |
| `flags[0]` | initialized, clean, sb csum crc32c, error action ro, btree node size, gc_reserve 8 %, meta/data csum crc32c, 1/1 replicas wanted, POSIX ACL | measured |
| `flags[1]` | str_hash siphash (2), 32-bit inode numbers, `encoded_extent_max` 7 (64 KiB), 1/1 replicas required | measured |
| `flags[3]`, `flags[4]` | shard inode numbers, inodes use key cache, journal flush delay 1000 ms; reclaim delay 100 ms, transaction names | measured |
| `flags[5]` | version upgrade complete = 1.3 | measured |
| layout | three slots, `sb_max_size_bits` 11: sector 8, the slot after it, the last whole slot of the device | read |

`extents_above_btree_updates` and `btree_updates_journalled` are set while a
volume is mounted and cleared when it is marked clean (measured: absent after
the tool's shutdown, present in its pre-mount superblock).

**Sections**, in the order written: `members_v2` (one 120-byte `bch_member`),
`members_v1` (the same member truncated to 56 bytes, which the formatter still
writes), `replicas_v0`, `journal_v2`, `clean`, `errors` (empty).

The member: device size in buckets, bucket size, flags = data allowed
journal|btree|user, durability stored as 2 (one higher than meant, so zero means
"default"), **freespace initialised** — without it the checker skips the
freespace tree and a mount stops to build it (read: `bch2_check_alloc_key`
returns early).

`replicas_v0` declares btree, journal and user on device 0, in the order the
formatter keeps them (eytzinger order of the sorted entries: btree, journal,
user). A usage total naming a set the section does not declare is refused (read).

## The clean section and the journal entry

A volume written whole is a cleanly shut down volume. The clean section holds,
framed as `jset_entry`s (measured order):

1. `usage` × 6: inode count, key version 0, persistent reserved for 1–4 replicas;
2. `data_usage` per replicas entry: btree sectors, journal 0, user sectors;
3. one `dev_usage`: `buckets_ec`, the retired `buckets_unavailable`, then
   buckets / sectors / fragmented for all ten data types;
4. two `clock` entries;
5. one `btree_root` per non-empty tree.

The checker recomputes every one of these from the trees and reports any
difference (`fs has wrong nr_inodes`, `dev 0 has wrong user sectors`, …; read:
`bch2_gc_done`; checked — the negative-control test). Free buckets count in
`dev_usage`; fragmented is bucket size less dirty sectors for every non-free
bucket, so the partly used bucket after the first superblock contributes
(measured: 248 sectors on the tool's volume).

**One journal entry** sits in the first journal bucket: sequence = last sequence
= the clean section's `journal_seq`, flags = crc32c checksum and *not* `no_flush`,
the same entries as the clean section, no keys. A 1.3 reader would take the clean
section alone (read: `use_clean` in `bch2_fs_recovery`), but it compares the two
root by root when both are present, and a newer reader upgrading the volume
replays the journal instead and finds the roots there or nowhere. Without the
entry v1.39.6 saw an empty volume; with it, it checks clean. The checksum covers
the `jset` from its magic to its last entry; the magic is the superblock's
internal-UUID word XOR `0x245235c1a3625032`.

## B-tree nodes

One unpacked bset per node, bset `version` 1.3. A node holds at most its size
less one word: the driver keeps a word spare after the last key for its varint
decoder and counts a node filled to the last byte as over-full (read in v1.39.6
`btree_keys_u64s_remaining`; v1.39.6 aborts on such a node the moment it inserts
into it — found by the many-files test). Trees with no keys get no root; the
checker treats a missing root as an empty tree (measured: the tool's volume names
only eight roots).

## Allocation information

Derived in one place, `BcacheFsAllocationBuilder`, from what occupies each bucket:
the sectors before the first superblock and every superblock slot at its full
advertised size (sb), every journal bucket in full (journal), every b-tree node at
its full node size whatever was written of it (btree), every extent (user).

**`alloc_v4`** — 56 bytes (read; measured): `journal_seq`, `flags`, `gen`,
`oldest_gen`, `data_type`, `stripe_redundancy`, `dirty_sectors`,
`cached_sectors`, `io_time[2]`, `stripe`, `nr_external_backpointers`,
`fragmentation_lru`. `flags` carries `backpointers_start` = 7 (the value's own
length; read: anything below 6 is invalid). A bucket written to also carries
`need_discard` and `need_inc_gen` and `io_time` 1/1, as the kernel's trigger sets
them on every sector-count increase (read; measured `0x1F`). An *empty* bucket
must not carry `need_discard`: its data type would then have to be
`need_discard` (read: `alloc_data_type`). Only used buckets, and empty buckets
whose generation is not zero, have keys.

**Fragmentation LRU** — a movable (btree/user) bucket less than full has
`fragmentation_lru = dirty × 2³¹ / bucket_size` and a `KEY_TYPE_set` in the LRU
tree at inode `0xFFFF << 48 | fragmentation_lru`, offset = bucket (read:
`alloc_lru_idx_fragmentation`, `lru_pos`; the checker verifies every LRU key
against its bucket).

**Freespace** — an extents-style tree of `KEY_TYPE_set` runs over free buckets,
keyed by where the run ends; the top byte of the position carries
`(gen − oldest_gen) >> 4` (read: `alloc_freespace_pos`), so a run breaks where
that changes.

**bucket_gens** — a missing key reads as generation zero (read: `alloc_gen`), so
only a run of 256 buckets holding a reused bucket gets a key. The formatter writes
none (measured).

**Backpointers** — one per extent and one per b-tree node, 32-byte value
(read): `btree_id`, `level`, `data_type`, a 40-bit `bucket_offset`, `bucket_len`,
the position of the key holding the pointer. Positioned at
`(bucket_start_sector << 10) + bucket_offset`, where `bucket_offset` is the offset
into the bucket shifted up by ten plus the checksummed-region offset — the shift is
the fixed `MAX_EXTENT_COMPRESS_RATIO_SHIFT` at 1.3 (read; measured
`6553600 = 6400 << 10`). The region offset is the crc entry's `offset`: a partial
overwrite leaves two keys pointing at the start of one checksummed region, live
from different offsets into it, and the offset is what keeps their backpointers
apart (read: `bch2_extent_ptr_to_bp`; kernel). `bucket_len` and the bucket's dirty
sectors are `ptr_disk_sectors`: the key's size, or for a compressed region the
size scaled by compressed over uncompressed and rounded up (read). A node's backpointer gives the level of the *pointer* to
it (node level + 1) and that pointer's position, which is the node's max key
(measured: level 1 for every root leaf on the tool's volume).

## Keys of the namespace

Unchanged from the earlier writer and confirmed by the checker: `inode_v3`
(fields up to `bi_subvol`, the inode's string-hash field 3 = siphash keyed by the
seed itself, link count stored less 1 for files and 2 for directories), dirents
hashed with SipHash-2-4 shifted right by one, extents keyed by their end sector
with a crc32c (type 5, seed 0) entry before the pointer, one extent per bucket at
most 128 sectors. Subvolume 1 (40 bytes) → snapshot `U32_MAX` → snapshot tree 1.

**Times, owner, group, mode.** The four time fields are 96 bits — a varint for
the low 64, one for the high 32 — and hold a signed count of the superblock's
`time_precision` nanoseconds from `time_base_lo`, two's-complement in the low
half (read: `bch2_time_to_timespec`; measured: the tools store a source file older
than the format as a negative count with a zero high half). This writer sets
`time_precision` 100 and `time_base_lo` 0, so a unit is one .NET tick since the
epoch. Owner and group are fields 5 and 6; the permission bits share the mode
with the inode kind in `bi_flags`. Confirmed through FUSE (`stat`: owner, group,
mode, atime/mtime/ctime to the 100 ns), and against a volume v1.39.6 populated
from a tree with foreign owners, nanosecond and pre-epoch times. The FUSE front end
shows any time before the epoch as the epoch, so pre-epoch times are proven
through the tool-written volume, not through a mount.

**Extended attributes** are `KEY_TYPE_xattr` (11) keys in the xattrs tree (3),
at the owning inode and an offset from the inode's string hash: SipHash-2-4 keyed by
the inode's hash seed over the namespace byte followed by the name, shifted right
by one, a collision taking the next free offset (read: `bch2_xattr_hash`,
`bch2_str_hash_end`). The value is `x_type`, `x_name_len`, `x_val_len`
(little-endian 16 bits), the name without its prefix, the value, padded to whole
words. Namespaces: `user.` 0, `system.posix_acl_access` 1, `…_default` 2,
`trusted.` 3, `security.` 4 (read: `KEY_TYPE_XATTR_INDEX_*`). A key holds at most
255 words with its five-word header, so header, name and value come to at most
2000 bytes (read: the `U8_MAX` check in `bch2_xattr_set`). ACLs are stored in
bcachefs's own encoding, not the xattr one, and are not written here. Confirmed by
the kernel: `getfattr` shows every attribute written here, a 1900-byte value
included, and the kernel's own `setfattr`/`setfattr -x` read back here.

**Symbolic links** keep the target as the inode's data, written through the page
cache with its terminating NUL, so the size is the target length plus one (read:
`bch2_symlink` → `page_symlink(…, strlen + 1)`; confirmed through FUSE:
`readlink` returns the target, `stat` the mode `0777`).

## What the tools wrote, as read here

- `bcachefs format` packs every key against the node's format and appends a
  bset per write — 4 KiB blocks on the measured volume — so the reader takes keys
  from the recovered view (every visible bset, journal replayed), not from the
  first bset.
- Tool-written extents carry a crc32c entry; partial overwrites give the
  checksummed region an offset, which moves where the live data starts.
- A stretch of a file no extent covers is a hole and reads as zeroes (the sparse
  file in the v1.39.6 reference volume).
- Compressed extents are decoded and every checksummed extent is verified on
  read; encrypted extents are refused per file (see below).

## Compressed and checksummed data

Read in v1.3.4 `compress.c`, `checksum.c`, `extents.h` and `bcachefs_format.h`;
confirmed against volumes the tools and the 6.8 kernel wrote
(`Compression.Tests/BcacheFs/ReferenceVectors/README.md`).

**The encoded region.** An extent value is a list of entries; a `crc32`,
`crc64` or `crc128` entry describes the pointers that follow it. The pointer
names the first sector of the *encoded region*, which is `compressed_size`
sectors long on disk and decodes to `uncompressed_size` sectors. The key's own
size is the live part: it starts `offset` sectors into the *decoded* data. All
three sizes are stored less one, in 7, 9 or 13 bits for the three entry widths
(read). An extent with no checksum entry is plain sectors at the pointer.

| Entry | compressed / uncompressed / offset bits | nonce | csum type, compression type | checksum |
|---|---|---|---|---|
| `crc32` (type bit 1) | 7 / 7 / 7 from bit 2 | — | bits 24, 28 (4 each) | high 32 bits of the word |
| `crc64` (type bit 2) | 9 / 9 / 9 from bit 3 | 10 bits at 30 | bits 40, 44 | second word low 64, top 16 bits of the first word high |
| `crc128` (type bit 3) | 13 / 13 / 13 from bit 4 | 13 bits at 43 | bits 56, 60 | the 16 bytes after the header |

**Checksums** cover the whole encoded region as it lies on disk — compressed
bytes and the zero padding up to a block boundary included (read:
`bch2_checksum_bio` over `compressed_size` sectors). Types: 1 `crc32c_nonzero`
(seed and final XOR `0xFFFFFFFF`), 2 `crc64_nonzero` (seed and final XOR all
ones), 5 `crc32c` (seed 0, no XOR), 6 `crc64` (seed 0, no XOR), 7 `xxhash`
(XXH64, seed 0). CRC-64 is the big-endian (non-reflected) ECMA-182 polynomial
`0x42F0E1EBA9EA3693`. A `crc32` entry holds the 32 low bits; the wider entries
hold the result in the low word, and nothing in the high word for these types.
Types 3 and 4 are ChaCha20/Poly1305 MACs over encrypted data.

**Compression types**: 0 none, 1 `lz4_old`, 2 `gzip`, 3 `lz4`, 4 `zstd`,
5 `incompressible` (a hint for rebalance: the data is stored plain). On disk:

- `lz4`, `lz4_old` — one raw LZ4 *block* (no frame), zero padded to the block
  size. The kernel decodes it with the partial decoder, which stops once
  `uncompressed_size` bytes have come out, so the padding is never parsed. Both
  types decode the same way.
- `gzip` — despite the name, raw DEFLATE (`-MAX_WBITS`: no zlib or gzip
  header, no trailer), zero padded; decoding ends at the final block.
- `zstd` — a little-endian 32-bit length, then one zstd frame of exactly that
  many bytes, then padding. The length is there because the decoder needs the
  exact input size (the source says so).

Every decoder must produce exactly `uncompressed_size × 512` bytes; anything
else is an I/O error in the kernel and a read error here. The writer compresses
at most `encoded_extent_max` (64 KiB by default) per extent and only stores the
result when it is at least one block smaller.

**Inline data** — `KEY_TYPE_inline_data` (17) in the extents tree: the value
*is* the data, starting at the first value byte, its length the value's length
in whole words (the tail beyond the inode size is padding). The key covers
`size` sectors ending at its position like any extent; anything past the data
reads as zeroes. `indirect_inline_data` (19) is the same thing in the reflink
tree, reached through a `reflink_p`.

**Reflinks** — `cp --reflink` moves the file's data into the **reflink tree** (btree 7):
`KEY_TYPE_reflink_v` (16) keys at inode 0, positioned like extents by the sector one past their
end, whose value is a 64-bit **refcount** followed by an ordinary extent's entries (crc, pointer).
Every name of the data then holds `KEY_TYPE_reflink_p` (15) keys in the extents tree: the value is
`idx` (u64), `front_pad` (u32), `back_pad` (u32); the file stretch `[end - size, end)` reads the
reflink tree's `[idx, idx + size)`, and the key holds a reference to `[idx - front_pad, idx + size +
back_pad)`. A partial overwrite of a clone splits its `reflink_p` around a new plain extent and pads
the halves so they still reference the whole indirect extent. Measured on volumes the 6.8 kernel
reflinked: a `reflink_v`'s refcount is the number of `reflink_p` keys whose padded range overlaps
it, and its backpointer names btree 7 and the indirect key. The reader cuts an indirect extent at
its front the way it cuts a partly overwritten extent; a stretch no indirect extent covers is
refused, as the kernel reports a missing indirect extent.

**Encryption** needs the volume key (ChaCha20, MAC by Poly1305) and this
repository has no implementation of either, so a file with an encrypted extent
is listed but refused with that reason. An unknown compression or checksum type
is refused the same way rather than guessed at.

## What the kernel does to it

Measured on volumes this package wrote, mounted read-write by 6.8 and unmounted:

- it upgrades the superblock to **1.4 (`member_seq`)** the moment it mounts, a
  compatible upgrade that touches no tree; bcachefs-tools 1.3.4 still checks the
  result;
- it packs its keys and appends bsets to existing nodes, and leaves journal
  entries behind its clean shutdown;
- a small file and a symbolic link's target become **`KEY_TYPE_inline_data`** (17):
  the bytes live in the extent key and occupy no bucket;
- it packs several files' extents into one bucket at different offsets, and a
  partial overwrite (`dd … conv=notrunc`) splits an extent into keys that share one
  checksummed region at different crc offsets;
- it keeps the times in the superblock's units (100 ns here) and sets ctime to the
  moment it changes an inode.

## In-place edits

Every edit re-publishes every tree from the keys it read, taken from the recovered
view (every visible bset, packed or not, the journal replayed), so it runs on any
volume whose every tree it either carries or rebuilds:

- carried key by key: extents (pointer extents, inline data, reservations, `reflink_p`), the
  reflink tree (`reflink_v`, `indirect_inline_data`; refcounts must equal the references found, and
  removing or replacing a file counts its references down, dropping an indirect extent at zero), inodes
  (`inode_v3`, and whatever else the tree holds that names no file), dirents,
  xattrs (with their inode: kept while it survives, dropped with it), subvolumes,
  snapshots, snapshot trees;
- rebuilt from what occupies the device: alloc, bucket_gens, freespace,
  need_discard, LRU, backpointers;
- refused with the reason: a volume not cleanly shut down, at a metadata version
  other than **1.3**, **1.4** or **1.39** (see *Editing 1.39*),
  edited without every member supplied, with a member not read-write, declaring replicas
  with a required count (`replicas_v1`), with keys in any other tree (quotas,
  stripes, logged ops …), or with an inode no directory reaches.

### Several members

A volume `bcachefs format` spread over several devices is edited through all of
them at once (`BcacheFsFormatDescriptor.AddToVolume` / `RemoveFromVolume`, read
with `OpenVolume`; the images in any order, each superblock says which member it
is). What changes against one device:

- Every allocation key is per member: alloc, freespace and `bucket_gens` at
  `(member, bucket)`, backpointers at `(member, sector << 10)`, and an LRU entry
  names its bucket as one word, the member above bit 48.
- Copies: `metadata_replicas` copies of every node and `data_replicas` of every
  extent, each on a different member allowed that data type (`data_allowed`),
  the members with the most free buckets first. A node pointer carries one entry
  per copy; an extent its crc32 entry and then one pointer per copy, the same
  bytes written to each.
- The clean section and the journal entry carry one `dev_usage` per member and
  one `data_usage` per replicas entry, which adds up the sectors of every copy; the
  `replicas_v0` section is rewritten as the entries it had plus every set the
  commit uses, the journal's included (the shutdown entry goes to every member
  with journal buckets).
- Every member's superblocks get the new sequence, and from 1.4 (`member_seq`)
  every member record gets it too: a 6.8 kernel opening the members together
  calls a device whose superblock is newer than its member record says
  "split brain" and refuses to mount (measured).
- A volume holding two copies of everything is read from either member alone.
- Members may differ in bucket size (`bcachefs format --bucket=64k a.img
  --bucket=128k b.img`): every allocation key, bucket number and data offset on a
  member is in its own buckets; a data run is never longer than the smallest
  bucket, metadata rows hold as many nodes as their smallest bucket, so a run or a
  node fits whichever members its copies land on.
- Members count by durability (`BCH_MEMBER_DURABILITY`, stored plus one): copies go
  to the most durable members first and as few are written as make up the replicas
  wanted — two copies wanted and a member of durability two: one copy, on it. A
  cache member (durability zero) gets no copy of data or metadata, and the cached
  pointers the kernel left on extents are dropped when the extents are carried (a
  cache copy is one the volume may discard), so their buckets come back free.

Defragmenting several members (`DefragmentVolume`) reads every live run (checksum
verified, compression decoded) into a temporary staging file, then lays each file's
runs down again in path order at the front (`ConsolidateAtStart`) or back
(`ConsolidateAtEnd`) of the members' data areas (past the metadata reservation,
never a superblock slot or the journal), filling member 0 before member 1 — data
moves between members — with `data_replicas` copies on distinct members, encoded as
the volume asks; holes and inline data stay. One metadata commit follows and the old
places are zeroed where nothing new landed. Other modes, metadata zone placement
and reflinked data are refused unchanged; a single member gets the single-device
pass with all its modes. Proven by `BcacheFsMultiDeviceTests` (fsck over all
members after gathering two members with one copy at the front — every run then on
member 0, in order — and at the back, two mirrored members, three members with two
copies; refusals unchanged), the kernel round trips below (each volume gathered at
the front and the back after the kernel and we wrote into it, fsck clean, the
kernel's listing equal to ours after each), and a mirrored 1.39 volume gathered and
checked by 1.39 fsck.

Different bucket sizes and durabilities are proven the same way: fsck over all
members after edits on 64k/128k members, three members of 64k/128k/256k with two
copies, durability 2+1 with two copies, and a cache member; the copies checked
key by key (one copy, on the durability-2 member; nothing on the cache member);
mirrored 64k/128k and durability-2 volumes gathered; and the kernel mounting
64k/128k, durability-2 and cache-member volumes it wrote into, after our edit,
reading what we read.

Proven for edits by `BcacheFsMultiDeviceTests` (fsck over
all members after edits on two members with one copy, two with two copies, three
with two copies, and two with two copies of 32 KiB nodes in 64 KiB buckets; each
member of a mirrored pair read alone; an edit through one member of two refused
and both unchanged) and `…MembersTheKernelWroteInto_WhenWeEditInPlace…` (Linux 6.8
mounted the members together and wrote into them, we edited through all of them,
fsck is clean, the kernel mounting them again reads what we read and keeps
writing). `bcachefs format` 1.3.3 does not finish formatting two members with
`--bucket=32k --btree_node_size=16k --replicas=2`, so that layout is not covered.

The volume's own layout is kept. Adds and removes work in whatever bucket size,
block size and journal placement the superblock names — `bcachefs format`'s
128 KiB default, `--bucket=32k` or `256k` with a matching `--btree_node_size`, its
4 KiB block — and write in its units:

- every node, data extent and the shutdown journal entry is a whole number of
  blocks (the kernel rejects a node pointer whose `sectors_written` is not, and
  so does our reader);
- a bucket holds its data as back-to-back extents of at most 128 sectors, the most
  a `bch_extent_crc32` entry's 7-bit size fields describe;
- no node goes into a superblock slot or a journal bucket, wherever they lie;
- a node smaller than a bucket (`--btree_node_size`) is written at that size, the
  nodes of one commit packed several to a metadata bucket; each costs its node
  size in the bucket's dirty sectors and in its backpointer;
- inode times are stored as the superblock's precision from its time base —
  nanoseconds from the instant `bcachefs format` ran, against this writer's 100 ns
  from the epoch (a time not on that grid rounds down).

Defragmentation and metadata relocation work in the same units: data moves in
whole buckets of the volume's size, never before the end of this writer's metadata
reservation (bucket 113), and relocated metadata keeps clear of the volume's
superblock slots and journal buckets wherever `bcachefs format` put them (on a
1 GiB volume the journal runs well past bucket 48), packing nodes several to a
bucket where they are smaller. Proven by `BcacheFsForeignLayoutTests` (fsck after
edits and a defragmentation with metadata placed before content on default,
32 KiB and 256 KiB volumes; the 1.3 reference volume edited, consolidated at start
and end and with its metadata moved to the back, then read back) and the
`…FormatVolumeTheKernelWroteInto_WhenWeEditInPlace…` kernel tests (Linux 6.8 wrote
into the formatted volume, we edited it, fsck is clean, the kernel reads what we
read and keeps writing; then two defragmentations, each followed by fsck and the
kernel's listing).

A file whose bytes are not touched keeps its inode and extents exactly: a sparse
file's sector count is what its extents hold, not its length rounded up. The
superblock keeps the version the volume had (1.4 after the kernel), and the journal
is replaced by the one shutdown entry.

New data goes into free buckets still at generation zero first (see *Upgrading
past 1.14*). Defragmentation first brings a volume the kernel wrote into the shape
this writer produces — every bucket past the metadata reservation holding the
extents of one file, back to back from its first sector, each checksummed region
exactly its live data (one 128-sector extent per 64 KiB of bucket) —
by copying the live bytes of every other extent into a bucket of its own with a
fresh crc32c; holes stay holes, inline data stays inline, and a compressed or
encrypted extent, or one longer than a crc32 entry holds, is refused rather than
decoded or split, before anything is written.

## Editing 1.39

A volume bcachefs-tools 1.39.6 wrote (metadata version 1.39,
`per_dev_fragmentation_lru`) is edited with its derived trees and superblock rebuilt
by that version's rules, measured with its `bcachefs list` / `show-super` and taken
as layouts from its format headers (constants only):

- **Accounting tree** (id 20) instead of the superblock's usage entries: a
  `KEY_TYPE_accounting` (34) per account, positioned at its `disk_accounting_pos`
  (type byte, then the type's packed fields) read as one 20-byte big-endian number
  over inode, offset and snapshot. Kept: `nr_inodes`; `replicas` (b-tree and user
  sets, sectors of every copy); `dev_data_type` (buckets, live sectors,
  fragmentation per member and type); `snapshot` (per snapshot id and extents /
  inodes / dirents / xattrs: keys, their bytes, data sectors); `btree` (node
  sectors of every copy, nodes, interior nodes); `inum` (per inode: extents, their
  size, on-disk sectors); `compression` (per type, "incompressible" included:
  extents, size, compressed size). A key whose counters are all zero is left out.
  The tree counts its own nodes, so it is part of the layout's fixed point.
- **alloc_v4** is eight words: `journal_seq_empty` and `stripe_sectors` replace the
  stored fragmentation, `backpointers_start` is 8.
- **Fragmentation LRU** per member: id `8192 + member`, time the fragmentation.
- **Backpointers** carry the bucket's generation and a flags word; the key shifts
  the sector by `BCH_SB_EXTENT_BP_SHIFT` (`flags[6]` bits 40–47; 16 on a volume
  1.39 formats, 10 when zero).
- **Superblock**: `replicas_v0` declares b-tree sets only (1.36,
  `no_sb_user_data_replicas`); the clean section and the shutdown journal entry
  carry the key version, the clocks and the roots, nothing else; each member's
  `btree_allocated_bitmap` gets the regions holding the new nodes (widened when a
  node lies past what 64 bits of `btree_bitmap_shift` cover); bsets and the jset
  say 1.39.
- **Journal sequence** goes on from the volume's last one: an inode records the
  sequence it was written at, and fsck calls one from a later sequence "in the
  future".
- Carried as they are: `logged_ops` (the inode allocation cursor; any other logged
  operation, one a crash left to resume, is refused) and `subvolume_children`.

- **New data matches the volume's IO options**, so it owes no reconcile work (from
  1.33 an extent that does not match carries a reconcile entry): each run of at most
  128 sectors is compressed with `background_compression`, else `compression`, by
  this package's own LZ4 (block), DEFLATE ("gzip", raw stream) or zstd (frame behind
  a 32-bit length) encoder, kept raw and marked *incompressible* where that is no
  smaller, and checksummed with `data_checksum` (crc32c in a crc32 entry, crc64 and
  xxhash in a crc64 entry, none without an entry when uncompressed). Encoded runs
  are packed back to back into the bucket, a run that does not fit going to the
  next. On a 1.3/1.4 volume data stays crc32c and uncompressed, byte for byte as
  before.

Refused, before anything is written: reservations (their 1.39 accounting is per
replica count), pending reconcile work (`bcachefs format --source` with two copies,
or with background compression, leaves some), and adding data to a volume asking
for erasure coding or a target, which would place it.

Proof is 1.39's own `fsck -n` (a 6.8 kernel stops at 1.4), with the tool named in
`CWB_BCACHEFS_EXTRA`: `BcacheFs139EditTests` edits a populated 1.39 volume, an
empty one with 128 KiB buckets, an empty mirrored one on two members, volumes
asking for lz4, gzip with crc64, zstd with xxhash, background zstd and no data
checksum (with a compressible file among the added ones), and the checked-in 1.39
reference volume, each fsck-clean afterwards; refuses a volume with pending
reconcile work unchanged. The checked-in 1.39 reference volumes (plain, lz4/crc32c,
gzip/crc64, zstd/xxhash) are edited and read back everywhere. fsck checks the
structure, accounting and checksums of the compressed runs, not that they decode:
every lz4, gzip and zstd run written into the reference volumes is decoded by a
reference decoder (the lz4 library's block decoder through Python `lz4.block`,
Python's zlib, the zstd tool) back to the file's bytes. An LZ4 block's length is
recorded nowhere (padding follows it), so the lengths from the padding-trimmed end
up are tried and one must decode to exactly the run. The lz4 library comes from
`~/.cache/cwb/lz4-venv` (`python3 -m venv ~/.cache/cwb/lz4-venv &&
~/.cache/cwb/lz4-venv/bin/pip install lz4`) or the system's Python.

## Upgrading past 1.14

v1.39.6 upgrading a volume prints `scanning for missing backpointers in 0/N
buckets, M buckets with no backpointers` and repairs nothing. Traced:

- On a fresh volume `M` is 1, and it is 1 on a volume `bcachefs format` 1.3.4 wrote
  too: the bucket is the node of the **accounting tree** (1.9) the upgrade creates
  itself (measured: `bcachefs list -b backpointers` after `fsck -y` shows exactly
  one new backpointer, `btree=accounting`, into the one new alloc key).
- From 1.14 a backpointer carries the bucket's generation and flags in the bytes
  where 1.3 kept the low 40 bits of `bucket_offset` (read: `struct
  bch_backpointer`, `check_bucket_backpointer_mismatch`). Upgrading, the pass
  discards every old backpointer whose "generation" does not match its bucket or
  whose "flags" are not zero, then rebuilds them from the extents. So every bucket
  **reused at a nonzero generation**, and every extent **not at its bucket's first
  sector**, is counted: measured 4 extra buckets after a remove-then-add that
  reused the freed buckets, 10 on a volume the kernel had written into (its own
  packed, offset extents count the same way).

A 1.3 backpointer cannot express either, so the remedy is placement: in-place edits
lay new data and metadata into buckets still at generation zero first, and the
writer starts every extent at a bucket boundary, so a volume created and edited
here reports the accounting node and nothing else
(`GivenOurVolumeEditedInPlace_WhenANewerToolUpgradesIt_ThenItRebuildsNoBackpointerOfOurs`).
A bucket reused because nothing else was free, a defragmentation that packs data
into freed buckets, and extents the kernel placed are still rebuilt by the upgrade,
as they are on any 1.3 volume.

