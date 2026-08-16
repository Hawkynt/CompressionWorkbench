# bcachefs accounting, backpointer and LRU keys

What a volume written here does not yet carry, what those keys look like, and how
that was established. Written down because the encoding is not obvious from the
headers alone and cost a day to work out; the next attempt should not have to
repeat it.

## Where this stands

`BcacheFsWriter` writes the alloc, bucket_gens and freespace trees. It does not
write accounting, backpointers or LRU. `bcachefs fsck -n` returns 0 on such a
volume with nothing found and nothing fixed, so their absence is not detectable
by the checker — it rebuilds them without complaint. `bcachefs list -b accounting`
on one of our images returns no keys, against 936 alloc keys on the same image.

That makes this a fidelity gap rather than a correctness one, and it is the
reason the trees are not written yet: adding an accounting key whose counters are
wrong turns a volume the checker accepts into one it rejects. Nothing here should
be implemented without checking each step against `fsck`.

## How the key positions are encoded

An accounting key's position is not a normal `bpos`. The format calls it a
`struct disk_accounting_pos` — a type-tagged union overlaid on the twenty bytes
of a `bpos` and treated as one twenty-byte integer, so that keys of a type sort
together.

The overlay is byte-reversed with respect to the struct. Taking the struct's
bytes as `s[0..19]`:

```
inode    = s[0]<<56 | s[1]<<48 | ... | s[7]
offset   = s[8]<<56 | ...      | s[15]
snapshot = s[16]<<24 | ... | s[19]
```

so the type tag, which is `s[0]`, lands in the most significant byte of `inode`.
Multi-byte fields inside the struct keep their native little-endian order; only
the placement of the bytes across the position is reversed.

Read off a real filesystem, formatted with `mkfs.bcachefs` and then initialised
by `bcachefs fsck -y`:

| key | position | check |
|---|---|---|
| `nr_inodes` | `POS_MIN` | type 0, so the whole position is zero |
| `dev_data_type dev=0 free` | `0x0300000000000000` | type 3 in the top byte |
| `dev_data_type dev=0 sb` | `+2^40` | `data_type` is `s[2]`, so bits 40–47 |
| `dev_data_type dev=0 journal` | `+2·2^40` | same field, value 2 |
| `btree btree=inodes` | `0x0601000000000000` | type 6, `u32 id` little-endian from `s[1]`, so id 1 is at bit 48 |

The types are `nr_inodes` 0, `persistent_reserved` 1, `replicas` 2,
`dev_data_type` 3, `compression` 4, `snapshot` 5, `btree` 6, `rebalance_work` 7,
`inum` 8, `reconcile_work` 9, `dev_leaving` 10. The counter count differs per
type — one for `nr_inodes`, three for `dev_data_type` — and is part of the type's
definition rather than the key.

## The counters are already known here

`dev_data_type` carries three: buckets, live sectors, and fragmented sectors. The
same real filesystem reports, for a 128 MB image with our geometry:

```
dev_data_type dev=0 data_type=free      1973    0     0
dev_data_type dev=0 data_type=sb          49  6152   120
dev_data_type dev=0 data_type=journal     16  2048     0
dev_data_type dev=0 data_type=btree       10  1280     0
```

Every one of those falls out of what the writer already computes for the alloc
tree. The 49 superblock buckets are the 33 at the front plus the 16 the trailing
slot spans; the 6152 live sectors are the 4104 the two front slots occupy plus
the 2048 of the trailing one; and the 120 fragmented sectors are
`49 × 128 − 6152`. In other words fragmented is
`buckets × bucket_sectors − live_sectors`, and the whole table is a fold over the
same per-bucket walk that produces the alloc keys.

That is the argument for doing this: the numbers are not new information, only a
second statement of information the volume already carries.

## LRU needs nothing

The LRU tree is empty on a filesystem `mkfs.bcachefs` made and the kernel
initialised — `bcachefs list -b lru` returns no keys at all. Ours is empty too,
so there is no difference to close. It is listed here so the next person does not
go looking for work that is not there.

## Backpointers: the encoding, and why they are not written yet

A real filesystem carries exactly one backpointer per b-tree node — ten keys
against the ten b-tree buckets on the control image, and nothing else, because
that filesystem holds no file data.

The position is

```
inode  = device index
offset = (bucket * bucket_sectors) << extent_bp_shift
```

with `extent_bp_shift` 16 on this filesystem: bucket 49 is sector 6272, and
6272 << 16 is 411041792, which is the position the control image shows. The
value is a `bch_backpointer` — `btree_id`, `level`, `data_type`, `bucket_gen`,
a `u32` of flags carrying the sub-offset, a `u32` bucket length, and a full
position — thirty-two bytes, which is the type's stated minimum value size. For a
node the length is the whole bucket, 128, and the position is `SPOS_MAX`.

Two things stop this being a half-hour change, and both are reasons to do it
deliberately rather than at the end of a session:

- **It is circular in a way the bucket count is not.** The backpointers tree is
  itself one of the trees being placed, and a backpointer names the bucket its
  node landed in — so the keys depend on a layout that depends on the keys. The
  existing fixed point settles how many b-tree buckets there are; this needs
  which node is in which bucket, which is one level finer.
- **Every node needs one, not every tree.** The control image has a single node
  per tree so the two look alike there. A tree that splits has a root and leaves
  at different levels, and the `level` field has to match the node it points at.

File data needs them too, keyed by the extent rather than `SPOS_MAX`, and the
control image cannot show what those look like because it holds no files.

Unlike accounting, `fsck` does check backpointers, so this one can be developed
against a real signal. That makes it a good next piece of work — and a bad one to
guess at, since a wrong key turns a volume the checker accepts into one it
rejects.

## What is not established

- The `replicas`, `snapshot` and `btree` accounting types appear in a real
  filesystem and their positions decode by the rule above, but their counters
  have not been derived from first principles here.
- Whether `fsck` accepts a volume carrying *some* accounting types and not
  others, or recomputes and reports a mismatch, has not been tested. It decides
  whether this can be done in pieces.
- Whether `extent_bp_shift` has to be written into the superblock or is derived
  from the encoded extent maximum. The control filesystem uses 16 and ours agrees
  by construction, but that is an observation, not a guarantee.
- `bcachefs fs usage` would be the natural check, and it refuses an unmounted
  image, so verification has to go through `fsck` or a real mount.

## References

- `/usr/src/bcachefs-v1.39.1/src/fs/bcachefs/alloc/accounting_format.h` —
  `disk_accounting_pos`, the type list, and the per-type counter descriptions
- `/usr/src/bcachefs-v1.39.1/src/fs/bcachefs/alloc/format.h` — `bch_alloc_v4`,
  `bch_bucket_gens`, and the data types the counters are broken out by
- The DKMS tree above is the version installed on the machine this was measured
  on; a different version may move fields, and the headers for the version in use
  are the ones that matter.
