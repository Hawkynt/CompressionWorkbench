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
allocated-byte count. Additional stat-data bytes are retained opaquely.
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

Add, remove, defrag, shrink and layout rebuild capture metadata before staging.
Existing stat-data extension bytes survive. Mode, ownership, link count and
timestamps are retained or applied through the typed metadata fields; size and
allocated-byte fields follow the rebuilt layout. `UpdateMetadata` stages explicit
metadata changes, including changes to the root. Existing object IDs survive,
and new IDs are allocated beyond the retained IDs rather than renumbering live
objects. Verification includes identity and typed fields as well as extension
bytes. Volume UUID and label, root stat-data extensions,
and empty directories are retained. Edits and defrag stage the target and verify
its complete native namespace and retained stat-data before replacing the source.
Unsupported or incomplete native trees are refused before editing. Shrink copies
the original through if a supported smaller result cannot be built.

Reading supports multiple internal levels. Writing builds multiple leaves and additional internal levels as needed. An
indivisible stat-data body or directory unit larger than a node is refused. This is not full Reiser4 metadata support:
separate xattr items, arbitrary object plugins, symlinks, sparse files, shared
objects, non-ASCII names and alternate node plugins are outside the profile.
Opaque stat-data retention does not prove the semantics of unknown extensions.
Native tree blocks discovered by traversal are reserved in the wipe map.

Validation uses reiser4progs 1.2.2 (Ubuntu package 1.2.2-1build2): native images
with 1, 100 and 5000 files in nested directories plus empty directories were
accepted by `fsck.reiser4 -y` as consistent. The largest has 166 leaves and
three tree levels. `Reiser4MutationExternalTests` additionally checks a wide
nested tree after Unix metadata updates, add/remove and defrag against
`fsck.reiser4` and `debugfs.reiser4`. Synthetic tests exercise stat-data tails
without claiming their unknown extension semantics are validated by the oracle.
