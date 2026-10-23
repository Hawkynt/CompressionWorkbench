# ZFS — on-disk notes for the reader

`FileSystem.Zfs.ZfsReader` is written from this page. The normative base is Sun's
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
column; the Q and R syndromes are not implemented, so RAID-Z2/3 needs every data
column present.

Mirror, replacing and spare vdevs hold full copies on every child. dRAID and indirect
(removed) vdevs are refused.
