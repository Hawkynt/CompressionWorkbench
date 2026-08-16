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

## What is not established

- The `replicas`, `snapshot` and `btree` accounting types appear in a real
  filesystem and their positions decode by the rule above, but their counters
  have not been derived from first principles here.
- Whether `fsck` accepts a volume carrying *some* accounting types and not
  others, or recomputes and reports a mismatch, has not been tested. It decides
  whether this can be done in pieces.
- The backpointer and LRU trees have not been looked at beyond confirming they
  are empty in our images.
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
