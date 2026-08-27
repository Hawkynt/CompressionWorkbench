# ReFS (`Refs`)

Microsoft ReFS 3.x volume image with namespace, allocation, in-place data relocation and filesystem-metadata placement support.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.refs` |
| Recognised extensions | `.refs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `52 65 46 53 00 00 00 00` | 3 | 0.85 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | no | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | yes | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `RefsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.


## How a volume is laid out

### RefsFormatDescriptor

Microsoft ReFS (Resilient File System) volume descriptor. Read/list/extract follows metadata reachable from the active checkpoint. Offline layout writes are coordinated by the ReFS placement manager, which can relocate file data, live MSB+ metadata and checkpoint pages while preserving the format-fixed VBR/SUPB bootstrap anchors.

### RefsMLogReader

Opens the native ReFS MLog from system OID 0x9/0xA, validates both control slots against the table's physical ranges, chooses the newest control by sequence/generation, and scans every 4 KiB record in the advertised ring. This is read-only until the log checksum/emission primitive is proven.

### RefsMLogWriter

Native circular MLog writer. It writes one fully checksummed LogCore data block, flushes it, then advances the alternate control slot. Checkpoint publication remains a separate later commit step.

### RefsExtentMap

Enumerates the active ReFS byte layout. Free space is fail-closed: a gap is free only when covered and clear in the on-disk allocator. ReFS structures are emitted individually so a filesystem-specific metadata mover can place movable pages while fixed bootstrap anchors remain hard reservations.

## Storage methods

- `resident` — Resident / inline
- `extent` — Extent-backed
- `stored` — Raw image

## Further reading

The implementation cites no sources. Adding a `<list type="bullet">` of them
to the descriptor's doc comment will bring them through to here.

