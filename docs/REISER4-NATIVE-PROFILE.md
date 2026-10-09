# Reiser4 native profile

## Evidence and provenance

The existing binary templates were captured from `mkfs.reiser4 -fffy` in
reiser4progs 1.2.2. Their fixed fields are the starting point for this profile.
The on-disk layout is also described by the Reiser4 node40, stat40, cde40,
extent40 and format40 plugins in the upstream
[reiser4progs project](https://github.com/edward6/reiser4progs).
The upstream GPL implementation is reference material, not copied or converted
source. This follows sourcing rung 2: no compatible managed implementation was
identified. The new traversal and rebuild tests use synthetic native nodes;
reference-tool conformance is covered separately as described below. Mounted
kernel interoperability has not been tested.

## Derived layout used here

For the captured 4096-byte format40 profile, the master is block 16 and the
format superblock is block 17. The latter stores the root block at byte 16 and
the tree height at byte 68. The root object's stat key has locality 41 and object
ID 42. Names beneath a directory use that directory's object ID as locality.

A node40 block has a 28-byte header, bodies growing upwards and 38-byte item
headers growing downwards from the end. The header's item count is at byte 2,
body-end offset at byte 6, magic at byte 8 and level at byte 26. Each item
header contains four little-endian key words, followed by body offset, flags
and plugin ID. Internal pointer bodies contain a little-endian block address.
Traversal follows all pointers, requires descending levels, and rejects cycles
and out-of-range addresses.

The supported object plugins are stat40 (0), cde40 (2) and extent40 (5).
Stat-data and directory items occupy level-1 leaves. Extents occupy level-2
twig nodes alongside child pointers; the reader also accepts the earlier
workbench placement of extents in leaves. This placement follows upstream
`libreiser4/tree.c` target-level selection, which assigns extent items to the
twig level and other object items to the leaf level.
Stat-data must include the lightweight and Unix extensions. Its first 44 bytes
carry extension mask, mode, link count, size, UID, GID, three timestamps and
allocated-byte count. Further extensions follow in mask-bit order; the writer
carries large times (bit 2, 12 bytes), flags (bit 5, 4 bytes), the plugin set
(bit 4) and the heir set (bit 8), each a 16-bit count of 4-byte slots, and
refuses a body holding any other extension or bytes its mask does not declare,
which `fsck.reiser4` reports as a fatal corruption.
Extent bodies contain pairs of 64-bit block start and block count. Multiple
items are joined in logical-offset order only when they describe contiguous
logical content. Holes and unsupported body plugins are rejected.

Cde40 has a 16-bit entry count, 26-byte unit headers, and units containing a
24-byte target stat key plus the stored name when its key is hashed. Resolving
the target's locality and object ID distinguishes files and directories. Dot
entries are omitted from the archive namespace. Nested and empty directories
are represented explicitly. Large directories split at unit boundaries; each
item uses its first unit name key. Extent arrays split at run boundaries with
logical byte offsets. Leaves end before a twig extent key, so a child pointer does not span an
extent key range at its parent. Sorted leaf items pack into bounded leaves, and parent nodes
use the first key of each child as their delimiting key. Parent levels grow until
one root fits. Format40 height, allocator bitmaps, object counts and allocated
node blocks follow the resulting layout. Paths have at most 129 components to
bound directory recursion. This writer restricts names to ASCII; it rejects
unsupported names rather than truncating them into different names.

## Rebuild contract and limits

Add, remove, defrag, shrink and layout rebuild capture metadata before staging,
and write nothing to the source until the staged volume has been read back and
verified. Volume UUID, label and mkfs id survive, as do existing object IDs;
new IDs are allocated beyond the retained ones rather than renumbering live
objects. Mode, ownership and timestamps are retained, size, allocated bytes and
directory link counts follow the rebuilt namespace, and the declared
stat-data extensions listed above are carried byte for byte. Root stat-data
(with its plugin set) and empty directories are retained.

`UpdateMetadata` sets mode permission bits, UID, GID and the three timestamps
through the same staged rebuild, including on the root. It refuses a change of
object kind, of link count (which follows the namespace) and of extension bytes
with `NotSupportedException`, the volume unchanged.

A rebuild writes the captured mkfs prefix back with only the identity patched
in, so it is refused, before anything is written, when the source's fixed
blocks hold anything else:

| Block | What must match the captured profile | Why |
| --- | --- | --- |
| 16 master | everything but UUID and label | other disk-format fields are not reproduced |
| 17 format40 | magic, tail policy, flags, version | `mkfs.reiser4 -o formatting=tails` changes the policy |
| 19, 20 journal | blank | a kernel-written journal is not replayed |
| 21 status | "consistent" | a volume fsck marked damaged is not silently cleared |
| 22 backup | everything but UUID, label, block count, mkfs id | it backs up the root's plugin set: hash, fibration, formatting |

Writing the default profile over another one was observed to fail:
`fsck.reiser4` reported the root's plugin set disagreeing with its backup, and
with `-o hash=tea_hash` a long name keyed with r5 could not be looked up by
`debugfs.reiser4`. Such volumes are read, not rewritten. Unsupported or
incomplete native trees are refused the same way. Shrink copies the original
through when it cannot build a supported smaller result.

Reading supports multiple internal levels. Writing builds multiple leaves and
additional internal levels as needed. An indivisible stat-data body or
directory unit larger than a node is refused. This is not full Reiser4
metadata support: separate xattr items, tail and ctail bodies, arbitrary
object plugins, symlinks, sparse files, shared objects, non-ASCII names,
short keys and alternate node plugins are outside the profile. Native tree
blocks discovered by traversal are reserved in the wipe map.

## Evidence

Oracle: reiser4progs 1.2.2 (Ubuntu package 1.2.2-1build2) under WSL. The WSL
kernel (6.6) has no Reiser4 driver, so nothing was mounted; volumes written by
the tools themselves come from `mkfs.reiser4` and from
`fsck.reiser4 --build-fs`. File bytes are taken where `debugfs.reiser4 -i`
says they are (its own path lookup, stat data and extent units); `debugfs -k`
is not usable as a byte oracle because it hands file contents to `printf` as
the format string.

| Volume | Checked by | Result |
| --- | --- | --- |
| 3,004 files in 28 nested directories, empty and deep-empty directories, sizes 0/1/4095/4096/4097/2.2 MB, names of 23, 24, 100 and 255 characters | `fsck.reiser4 --check`; `debugfs -s`; `debugfs -i` + block copy for 316 files; `measurefs -S` | consistent; height 3, 38 twigs, 113 leaves, 3,040 objects; 0 byte mismatches |
| the same after metadata update, add (nested), remove (subtree and a 255-character name), defrag, shrink, relayout | `fsck`; `debugfs -s`, `-i` and `-k` before/after | consistent each time; UUID, label, stat data and stat keys (object IDs) of untouched objects identical; the update applied exactly |
| 12,000 files with extents | `fsck`; `debugfs -s` | consistent; height 4 |
| our volume after `fsck.reiser4 --build-fs` inserted lost+found (object 0xffff, uid 1000) | our reader; then add into lost+found and `fsck` | read completely, 0 mismatches; consistent, object ID and owner kept |
| `mkfs.reiser4` volume, then add nested files, long names, an empty directory | `fsck`; `debugfs -s`, `-i` | consistent; UUID, label, mkfs id, policies and root stat unchanged |
| `mkfs.reiser4 -o hash=tea_hash` / `formatting=tails` / `fibration=lexic_fibre` | our editor | read; edit refused, image byte-identical |
| a file carrying a large-time extension (mask bit 2) through add and defrag | `fsck`; `debugfs -i` | consistent; `sdext_lt` still present |

`Reiser4ProfileExternalTests` and `Reiser4MutationExternalTests` replay these
checks wherever reiser4progs is installed.
