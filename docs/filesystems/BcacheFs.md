# BcacheFS (`BcacheFs`)

BcacheFS Linux filesystem image — native b-tree R/W with true in-place add/replace/remove, purge, defragment, optimize/layout maintenance and free-space/slack wiping.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.bcachefs` |
| Recognised extensions | `.bcachefs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `C6 85 73 F6 66 CE 90 A9 D9 6A 60 CF 80 3D F7 EF` | 4120 | 0.85 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | yes | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | yes | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `BcacheFsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### BcacheFsFormatDescriptor

Full workbench descriptor for the single-device bcachefs profile implemented here: native b-trees, true in-place CRUD, allocation/accounting maintenance, in-place defragmentation, purge and unused-space wiping.

### BcacheFsReader

Reads the files a bcachefs volume holds.

There is no directory to walk and no inode table to index. Names come from the dirents tree, each key of which sits at a position made of its directory's inode and a hash of the name; sizes come from the inodes tree; and the bytes come from the extents tree, whose keys are positioned by the inode and the sector one past the end of what they cover. A path is rebuilt by joining the three.

### BcacheFsWriter

Writes a bcachefs volume: a superblock, the b-trees that describe the files, and the files themselves.

bcachefs keeps no directory blocks and no inode table. A file's name is a key in the dirents tree, its metadata a key in the inodes tree, and its bytes are named by keys in the extents tree; a volume is those trees plus a superblock that says where their roots are. Because the volume is written whole and never mounted for writing in between, the roots go in the superblock's clean section, and no journal entries are needed to find them.

The allocation information is written too: the alloc tree says what each bucket holds and how much of it is used, the bucket_gens tree gives every bucket's generation, and the freespace tree covers the runs of buckets nothing has been laid into. What each bucket holds has to agree with what the extents say, and the count of b-tree buckets feeds itself — those keys are themselves keys, so adding them can want another node — which is why the description is settled by repetition rather than worked out once.

It used to be left out, and the volume claimed no_alloc_info and small_image to be let past the check that would have built it. No formatter sets either, so the volume could be told from one by the bits alone, and a read-write mount was refused outright. Neither is claimed now.

The accounting tree carries the totals: how many inodes there are, and per kind of content how many buckets, how many live sectors and how many sectors sit unused inside used buckets. They come off the same walk as the alloc keys, because they are the same facts added up, and counting them separately is how the two come to disagree.

Accounting is the one part here the checker will not confirm. A volume carrying wrong totals passes fsck exactly as one carrying right ones does — tested, not assumed — so the numbers are held instead to a filesystem mkfs.bcachefs made and the kernel initialised. Against that, the superblock and journal rows match to the sector.

The backpointers tree points the other way: from a stretch of the device back to what occupies it — one key per b-tree node, and one per file extent. Those keys have to know where each node landed, which is decided by the same rule the write pass follows — trees in order, each taking as many consecutive buckets as it has nodes — so the rule is applied here rather than the assignment being threaded out of the writer.

Accounting also carries what each tree costs and what each snapshot holds in it. Both are read off the trees themselves, so they cannot come to describe a volume other than the one being written, and the accounting tree measures itself among them.

The LRU tree stays empty, which is what a filesystem mkfs.bcachefs made and the kernel initialised also has, so there is nothing there to write.

The replicas counters say how many sectors of each kind of content there are per set of devices holding a copy of it, and the superblock's replicas section declares those sets. The two go together: a counter naming a set the section does not declare is refused, and the volume with it. See docs/BCACHEFS-ACCOUNTING.md.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `128 MB`, `256 MB`, `512 MB` | Total image capacity. Must be at least 128 MB so the superblock copies fit. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 31 chars). |

## Storage methods

- `stored` — Stored

## Further reading

The implementation cites no sources. Adding a `<list type="bullet">` of them
to the descriptor's doc comment will bring them through to here.

