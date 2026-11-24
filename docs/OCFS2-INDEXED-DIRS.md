# OCFS2 indexed directories

What the in-place editor keeps when it adds or removes a name in an indexed directory, as the
Linux kernel's `ocfs2` driver lays one out. Written from the kernel sources (GPL, read as
reference material only) and checked against volumes the kernel filled (`Compression.Tests/Ocfs2/
ReferenceVectors`) and against the kernel and `fsck.ocfs2` after every edit.

## Where things are

- **Directory inode** — `i_dyn_features` bit 0x0008 (`OCFS2_INDEXED_DIR_FL`) marks it indexed;
  `i_dx_root` (u64 at 0x88) names the index root block. Its own leaf extent list (in `id2`) names
  the directory's 4 KiB blocks of entries, as for any extent-backed directory.
- **Directory block trailer** — the last 64 bytes of every block of a directory on an
  `indexed-dirs` volume: a fake entry (inode 0, `rec_len` 64 at 0x08) so older readers step over
  it; `db_free_rec_len` (u16 at 0x0E); signature `DIRTRL1` (0x10); `db_free_next` (u64 at 0x20);
  `db_blkno` (0x28); `db_parent_dinode` (0x30); a block check (0x38, zero without metaecc).
  Entries end where the trailer begins.
- **Index root** — signature `DXDIR01`; `dr_clusters` (u32 at 0x28); `dr_flags` (0x2C, bit 0 =
  inline); `dr_dir_blkno` (0x30); `dr_num_entries` (u32 at 0x38, "." and ".." included);
  `dr_free_blk` (u64 at 0x40, head of the free list). From 0xC0 either an inline entry list
  (u32 reserved, u16 count, u16 used, then 16-byte entries) or an extent list of leaf clusters
  (depth u16, count u16 — 243 at 4 KiB —, next free record u16, records of cpos u32, clusters u16,
  flags, block u64).
- **Index leaf** — signature `DXLEAF1`; `dl_blkno` (0x10); the volume's generation (0x18); an
  entry list at 0x28 (count 253 at 4 KiB, used, entries from 0x30).
- **Index entry** — major hash (u32), minor hash (u32), the directory block holding the name (u64).

## The name hash

ext3's TEA hash. The state is four words: `s_dx_seed[0..2]` (superblock 0xBC..0xC7, relative to
`id2`) and `s_uuid_hash` (0x44). The name is taken 16 bytes at a time; each block is packed into
four words — a pad word holding the remaining length in every byte, each byte folded in as
`value = byte + (value << 8)` (unsigned), a word stored every four bytes, unused words left as the
pad — and run through 16 rounds (`sum += 0x9E3779B9; b0 += ((b1<<4)+a) ^ (b1+sum) ^ ((b1>>5)+b);
b1 += ((b0<<4)+c) ^ (b0+sum) ^ ((b0>>5)+d)`), whose results are added to the first two state words.
Major is the first word, minor the second. "." and ".." hash to zero. Every name of the kernel's
reference volume hashes to an entry the kernel wrote.

## Finding a leaf

The last extent record whose cpos is at or below the major hash; the cluster at the hash's offset
into it (its last cluster past its end); the block within the cluster by the minor hash masked with
the blocks per cluster less one (one block per cluster at 4 KiB/4 KiB). Leaf entries are not kept
sorted; lookups scan.

## Adding and removing

- A name goes into the first block of the free list whose free length and actual hole take it.
  The block's largest hole is recomputed (an unused entry counts whole, a used one less the 4-byte
  aligned room its name needs, anything under 16 bytes as none); a block left without one leaves
  the list (from the root's head or the previous block's trailer). Its index entry is appended to
  the inline list or to its leaf, and `dr_num_entries` counts it.
- A full inline list moves into one new leaf cluster at cpos 0: the root's inline flag clears, its
  list becomes an extent list of that one record, `dr_clusters` becomes 1.
- A full leaf is sorted by major then minor hash and split: if every entry shares one major hash,
  that is refused when it is the new name's, else the split is just past the leaf's cpos when it is
  that, else the larger of it and the new name's; otherwise the split is the first major hash from
  the median on that exceeds the leaf's cpos. A new cluster is recorded at that cpos right after
  the leaf's record, `dr_clusters` grows, and every entry from the split on moves into it.
- A directory without room in its free list takes one more block: one empty entry up to the
  trailer, the trailer filled in, the block pushed onto the head of the free list.
- Removing a name splices its entry into the one before it (or blanks the first), recomputes the
  block's hole, pushes the block onto the free list if it was not on it, and takes the index entry
  out (later entries shift down), `dr_num_entries` counting down.

Refused before anything is written: index or directory extent trees deeper than their root, a
full leaf in a multi-cluster record, a split that would still leave no room, and growing a full
inline directory on an `indexed-dirs` volume (the kernel would index it).
