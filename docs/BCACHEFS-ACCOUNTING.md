# bcachefs accounting, backpointer and LRU keys

What a volume written here carries, what is still missing and why, and how each
encoding was established. Written down because none of it is obvious from the
headers alone; the next attempt should not have to work it out again.

## Where this stands

`BcacheFsWriter` writes the alloc, bucket_gens, freespace, accounting and
backpointers trees. On a small volume that is

```
alloc: 158   freespace: 2   bucket_gens: 8   accounting: 20   backpointers: 12
```

with `bcachefs fsck -n` returning 0, nothing found and nothing fixed.

The two halves are checked by different things, and it matters which is which.
The checker validates backpointers: a key naming the wrong bucket, tree or level
is refused, so that half is developed against a real signal. It does *not*
validate the accounting totals — a volume carrying wrong numbers passes exactly
as one carrying right ones, tested rather than assumed — so those are held to a
filesystem `mkfs.bcachefs` made and the kernel initialised, which is the only
thing that will contradict them.

Accounting's `btree` and `snapshot` types are written as well: what each tree
costs in sectors, nodes and inner nodes, and how many keys of each snapshot sit
in each tree with their total key bytes. Both are read off the trees themselves.

The `replicas` counters are written too, together with the superblock section
that has to declare the device sets they name. LRU needs nothing.

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

## Backpointers

Written now. What follows is what it took, because the shift is the part that
looks settled and is not.

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

The shift is not a constant. It is `BCH_SB_EXTENT_BP_SHIFT`, bits 40 to 48 of
the superblock's `flags[6]`, and it reads as ten when left unset. A formatter
writes sixteen. The first attempt here wrote keys shifted by sixteen without
writing the field, so the checker read them back shifted by ten and placed every
node sixty-four buckets too far along:

```
ours: bucket=0:3136:0 btree=extents ...    backpointer_to_missing_alloc 12
real: bucket=0:49:0   btree=alloc ...
```

Writing sixteen into the superblock as well brought the two into line — the
field was another difference from a formatted filesystem in its own right, since
ours had been leaving it at zero.

Which node lands in which bucket is decided by the write pass: trees in order,
each taking as many consecutive buckets as it has nodes, leaves before the root.
The keys need that assignment before anything is written, so the rule is applied
in the plan rather than the assignment being carried out of the writer. It is
circular — the backpointers tree is itself one of the trees being placed — and
the existing fixed point over the b-tree bucket count already absorbs it.

Unlike accounting, `fsck` does check these, which is how both mistakes above were
caught rather than shipped.

## Replicas needs its superblock section written with it

A `replicas` counter names a set of devices holding a copy of some content, and
that set has to be declared in the superblock before a counter may refer to it.
Writing the counters alone is refused, and the volume that had been passing
cleanly stops passing:

```
accounting_read... accounting not marked in superblock replicas
  accounting_replicas_not_marked  2
```

So the two are written together. The section is `replicas_v0`, superblock field
type 3, holding entries of `{u8 data_type, u8 nr_devs, u8 devs[]}` — three bytes
each here, there being one device. A formatted filesystem carries the same
section, which ours had been missing on its own account.

The counter's position is a `bch_replicas_entry_v1`,
`[2, data_type, nr_devs, nr_required, dev…]`, so one device holding b-tree data
is `[2, 3, 1, 1, 0]`. That comes out at the same position the control filesystem
uses, to the byte:

```
ours:    144960716812582912 : replicas btree: 1/1 [0] 1536
control: 144960716812582912 : replicas btree: 1/1 [0] 1280
```

with a second entry for user data that the control has no counterpart for,
holding no files.

## What is not established

- Backpointers for file data. A node's are written and checked by the checker;
  an extent's are keyed by the extent rather than `SPOS_MAX`, and the control
  filesystem holds no files, so it cannot show what those look like.
- The `compression`, `inum` and `rebalance_work` accounting types. None appears
  in the control filesystem, so there is nothing here to check them against.

Two questions that were open have since been answered, and are recorded so they
are not asked again. `fsck` does accept a volume carrying some accounting types
and not others — it neither recomputes nor reports a mismatch — which is what
makes this safe to do in pieces. And `extent_bp_shift` is a superblock field,
`BCH_SB_EXTENT_BP_SHIFT` in `flags[6]`, not something derived at mount time.

## References

- `/usr/src/bcachefs-v1.39.1/src/fs/bcachefs/alloc/accounting_format.h` —
  `disk_accounting_pos`, the type list, and the per-type counter descriptions
- `/usr/src/bcachefs-v1.39.1/src/fs/bcachefs/alloc/format.h` — `bch_alloc_v4`,
  `bch_bucket_gens`, and the data types the counters are broken out by
- The DKMS tree above is the version installed on the machine this was measured
  on; a different version may move fields, and the headers for the version in use
  are the ones that matter.
