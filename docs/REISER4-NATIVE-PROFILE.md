# Reiser4 native profile

## Evidence and provenance

The fixed blocks of a fresh volume are byte-exact captures from `mkfs.reiser4 -fffy` in
reiser4progs 1.2.2. Everything else in this document was derived from the behaviour of two
implementations and checked against both:

* **reiser4progs 1.2.2** (Ubuntu package 1.2.2-1build2) under WSL: `mkfs.reiser4` makes volumes with
  every plugin profile, `fsck.reiser4 --check` judges every volume the writer produces, and
  `debugfs.reiser4` resolves names (`-i` prints an object's stat data and items, `-k` prints a
  file through reiser4progs' own body plugins — usable as a byte oracle only for text without `%`,
  because the bytes are handed to `printf` as its format string).
* **A Reiser4 kernel**: Metztli's Debian build of Linux 5.4.18 with the SFRN 4.0.2 patch
  (`linux-image-5.4.0-3+reizer4.0.2-cloud-amd64`), booted under QEMU with a busybox initramfs
  (`Compression.Tests/Reiser4/KernelOracle/kernel-run.sh`). It wrote the reference volumes in
  `Compression.Tests/Reiser4/ReferenceVectors` through its own write path and reads back every
  volume the writer produces.

The upstream sources (the GPL [reiser4progs](https://github.com/edward6/reiser4progs) and
[kernel module](https://github.com/edward6/reiser4)) were read as reference material to write this
document; no code was copied, converted or transliterated (sourcing rung 2: no licence-compatible
managed implementation exists). Constants — magic strings, TEA key words and delta, FNV offset
basis and prime, the degenerate hash value, CRC-32C and Adler-32 — are copied exactly.

## Fixed blocks

Block size is 4096; the master superblock is block 16 and the format40 superblock block 17
(root block at byte 16, next object id at 24, file count at 32, mkfs id at 48, tree height at 68,
tail policy at 70, flags at 72 — bit 0 set for large keys — and node plugin id at 84). Block 18 is
the first allocator bitmap (a 4-byte Adler-32 of the rest, then one bit per block; further bitmaps
sit at multiples of 32 736 blocks), 19 and 20 the journal header and footer, 21 the status block
(`"ReiSeR4StATusBl"`, then a 64-bit status that is 0 for a consistent volume), 22 the backup of
the master, the format fields and the root's plugin set.

An edit carries blocks 16, 17, 21 and 22 of the source and patches only the counters (block and
free-block counts, root, next object id, file count, height); the journal blocks are written blank
because the rebuilt volume already holds the replayed state. A volume whose status is not 0 is
refused, as is one with short keys (`mkfs.reiser4 -o key=key_short` crashes in 1.2.2, so no
reference exists) or a block size other than 4096.

## Plugin profile

`mkfs.reiser4 -o` changes exactly three things: the format40 tail policy (block 17, byte 70), the
root directory's `sdext_plugin_set` and its copy in the backup block, and — for `node=node41` — the
node plugin id (block 17, byte 84) and the node layout. Plugin-set members are 16-bit pairs
(member, plugin id): 0 file, 1 directory, 2 permission, 3 formatting, 4 hash, 5 fibration,
6 stat data, 7 directory item, 8 cipher, 9 digest, 10 compression, 11 compression mode, 12 cluster,
13 create. An object's missing members come from the root's plugin set (the kernel's
`finish_pset`), and the root's missing ones from the kernel defaults (r5, ext_1, smart, lzo1, conv,
64K, reg40). The default mkfs profile names **ccreg40** (cryptcompress) as the create plugin, so a
kernel writes compressed files on a default volume.

### Directory entry keys

A directory entry's key is `(directory oid, type 0, ordering, objectid, offset)`. The name's bytes
pack big-endian into the ordering from its second byte (seven bytes), then the objectid (eight),
then the offset (eight). A name longer than 23 bytes sets bit 56 of the ordering, keeps only its
first 15 bytes in the key, and puts the hash of the remaining bytes in the offset; such a name is
stored after the entry's 24-byte target key, NUL-terminated. The fibration plugin's fibre occupies
the ordering's top seven bits:

| Fibration | Fibre |
| --- | --- |
| `lexic_fibre` (0) | 0 |
| `dot_o_fibre` (1) | 1 for names ending in `.o`, else 0 |
| `ext_1_fibre` (2) | the last byte, when the byte before it is `.` (and the name is longer than 2) |
| `ext_3_fibre` (3) | the sum of the last three bytes, when the fourth from last is `.` (name longer than 4), modulo 128 |

| Hash | Value of the bytes after the 15th |
| --- | --- |
| `r5_hash` (1) | `a = (a + (b << 4) + (b >> 4)) * 11` over the bytes, 64-bit |
| `tea_hash` (2) | TEA in a Davies–Meyer construction evaluated in 64-bit arithmetic: 16-byte blocks of four little-endian 32-bit words get 6 rounds, the remainder padded with `len | len << 8 | …` gets 10; the result is the two halves xored |
| `fnv1_hash` (3) | 64-bit FNV-1 |
| `deg_hash` (4) | the constant `0xC0C0C0C010101010` |
| `rupasov_hash` (0) | not computed: the kernel and reiser4progs disagree (fsck.reiser4 reports every kernel-keyed long name as carrying the wrong offset), so no long name keyed with it can satisfy both. Existing entries are carried; new names longer than 23 bytes are refused. |

The key of a new object's stat data is `(parent oid, type 1, ordering of its first name, oid, 0)`,
as the kernel makes it; directory entries carry the target's full stat key. Existing entries and
stat keys are always carried as found, never recomputed.

## Nodes and items

A node40 node has a 28-byte header (plugin id, item count, free space, free-space start, magic
`0x52344653`, mkfs id, flush id, flags, level) and 38-byte item headers growing down from the end
(32-byte key, body offset, flags, item plugin id). node41 adds a 32-bit checksum at byte 28 — the
CRC-32C register (initial value all ones, no final inversion) over the 28 header bytes and then
everything after the checksum — and its bodies start at byte 32.

| Item plugin | Level | Body |
| --- | --- | --- |
| stat40 (0) | leaf | the stat data |
| cde40 (2) | leaf | 16-bit count, 26-byte unit headers (three key words, body offset), units of a 24-byte target key plus the stored name of a long entry |
| nodeptr40 (3) | internal | a 64-bit block number |
| extent40 (5) | twig | 16-byte units: start block and width; start 0 is a hole |
| plain40 (6) | leaf | tail: the file's bytes from the key's offset |
| ctail40 (7) | leaf | one byte of cluster shift, then bytes of a disk cluster from the key's offset |

Stat data starts with a 16-bit extension mask; extensions follow in bit order: light-weight (bit 0:
mode, links, size), Unix (bit 1: uid, gid, atime, mtime, ctime, then 64-bit bytes — or the device
number `major << 20 | minor` of a device node), large times (bit 2, 12 bytes), symlink (bit 3: the
target, as long as the light-weight size, then a NUL), plugin set (bit 4) and heir set (bit 8)
(a count of 4-byte pairs), flags (bit 5, 4 bytes). The crypto extension (bit 7), a cipher in the
plugin set and a chained mask (bit 15) are refused: no key is available for encrypted files.

### File bodies

* **reg40, extents.** Units in logical order; holes are units with start 0 and are kept as holes.
  `bytes` counts allocated blocks. A unit never covers an allocator bitmap block.
* **reg40, tails.** plain40 items covering the file from offset 0; `bytes` equals the size. The
  kernel's smart policy keeps files of up to four blocks in tails.
* **ccreg40 (cryptcompress).** The file is cut into logical clusters of 4 KiB to 64 KiB (cluster
  plugin 4…0, shift 12…16, also stored as each ctail item's first byte). Each present cluster is
  a disk cluster split across ctail items keyed at the cluster's start plus the byte offset within
  the disk cluster. A disk cluster is compressed exactly when it is shorter than the logical
  cluster (the cluster size, or what is left of the file); then its last four bytes are the
  little-endian Adler-32 of the compressed stream before them. lzo1 is raw LZO1X, gzip1 a raw
  deflate stream, zstd1 a Zstandard frame. Compression is kept only when stream plus checksum is
  shorter than the input, and inputs under the plugin's minimum (256 bytes, 64 for gzip1) are
  stored plain. Missing clusters are holes. `bytes` counts clusters times the cluster size. The
  kernel's `conv` mode turns an incompressible file into a reg40 extent file and records
  `compressMode=latt` in the plugin set of one it compressed; both are carried.

### Objects

Directories, regular files, symlinks (target in the stat data), character and block devices,
FIFOs and sockets (no body), and hard links (several entries naming one stat key; `nlink` counts
them). A directory's `size` is its number of entries including the dot entries, its `bytes` the sum
of its units (26 + 24 bytes each, plus a long name and its NUL), its `nlink` 2 plus its
subdirectories (3 plus them for the root). The writer recomputes these, and a file's `nlink`, from
the namespace; every other stat-data byte is carried.

## Journal

Block 19 holds the last committed transaction, block 20 the last one written back to its home
locations (plus the free-block, file and next-oid counters). Every transaction between them is
replayed, oldest first: its head (`"TxMagic4"`, id, total records, padding, previous transaction,
first wander record, counters) links back to the previous one, and its wander records
(`"LogMagc4"`, id, total, serial, next record) list `(original, wandered)` block pairs from byte 32;
replay copies each wandered block over its original. Reading happens on the replayed view; an edit
writes the replayed state with a blank journal. A chain that does not parse is refused.

Verified on `journal-replay`, captured between a kernel commit and its write-back (writes throttled
by QEMU, image copied the moment the header moved past the footer): the replayed namespace is the
one the kernel shows after mounting, and differs from the unreplayed one.

## Writing

New regular files are extents by default; `FileBody` (`Reiser4Writer`, the `FileBody` create
option, `Add(…, Reiser4FileBody)`) selects tails, the smart policy, cryptcompress, or whatever the
volume's root says (`Profile`: its create plugin, then its formatting). A replaced file keeps its
identity, metadata and plugin. Leaves end before a twig extent key; parent levels grow until one
root fits at block 23; the first leaf is block 24.

Every edit is staged: the source is read whole (journal replayed), edited as a plan, written to
scratch, read back and verified — namespace, identity and every object's stat data — and only then
copied over the source. Refusals leave the source byte-identical.

### Defragmentation

The in-place pass moves extent data on trees of any height: each unit's run moves on its own, the
new start is written into the unit's twig (node41 checksums re-sealed), and the allocator bits of
the blocks each unit left are cleared and of the blocks it landed on set. Tree nodes, tails and
compressed clusters do not move. A volume whose journal holds transactions, or a file whose extent
covers blocks past its end, falls back to a staged rebuild at the same size.

## Evidence

| Volume | Checked by | Result |
| --- | --- | --- |
| 13 kernel-written reference volumes: default (lzo1, conv, 64K), gzip1+force+8K, zstd1+latt+16K, lzo1+none+4K, reg40 smart, reg40 tails, reg40+extents+node41, node41, tea+lexic, fnv1+dot_o, deg+ext_3, rupasov, journal replay | our reader against the kernel's read-back manifest | every path, type, mode, owner, mtime, link count, size, device, symlink target and file MD5 equal |
| the same, edited (add a long-named file under each body, a symlink, a hard link; remove two names) | `fsck.reiser4 --check`; the kernel's manifest | consistent; carried objects identical, new ones read back byte for byte, including clusters we compressed with lzo1, gzip1 and zstd1 |
| names in every reference directory | re-keyed with the volume's hash and fibration | identical to the kernel's keys (all but Rupasov) |
| every `mkfs.reiser4 -o` profile (hashes, fibrations, formattings, create, compression, mode, cluster, node41) edited under each body | `fsck.reiser4 --check`; `debugfs.reiser4 -i/-k` | consistent; names resolve; plain bodies read back; root plugin set unchanged |
| writer volumes with every object kind and body | `fsck.reiser4`; the kernel's manifest | consistent; identical |
| a tall tree scattered and defragmented in place | `fsck.reiser4`; the kernel's manifest | consistent after both; no node moved; every file identical |
| 3,004 files in 28 nested directories, empty and deep-empty directories, sizes 0/1/4095/4096/4097/2.2 MB, names of 23, 24, 100 and 255 characters | `fsck.reiser4 --check`; `debugfs -s`; `debugfs -i` + block copy for 316 files; `measurefs -S` | consistent; height 3, 38 twigs, 113 leaves, 3,040 objects; 0 byte mismatches |
| 12,000 files with extents | `fsck`; `debugfs -s` | consistent; height 4 |
| our volume after `fsck.reiser4 --build-fs` inserted lost+found | our reader; then add into lost+found and `fsck` | read completely; consistent, object ID and owner kept |

Known disagreements between the two oracles, recorded rather than resolved: kernel-written Rupasov
long names fail `fsck.reiser4`; a tails-formatted file the kernel left with a trailing hole is
reported by `fsck.reiser4` as having the wrong size. `debugfs.reiser4` keys lookups below the root with its own default profile rather than the root's plugin set, so in a non-default profile it finds hashed or fibred names in subdirectories — the kernel's as well as ours — only when given the volume's profile with `-o`.

`Reiser4KernelVolumeTests` runs without any tool. `Reiser4ProfileExternalTests`,
`Reiser4MutationExternalTests` and `Reiser4FeatureExternalTests` replay the reiser4progs checks
wherever it is installed; the Linux CI leg installs it and runs them as the hard "Reiser4 oracle"
gate with `CWB_REQUIRE_REISER4PROGS=1`, so a missing tool fails the gate instead of skipping it.
`Reiser4KernelOracleTests` need the kernel (`CWB_REISER4_KERNEL_DOWNLOAD=1` fetches it) and skip
without it.
