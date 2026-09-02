# SGI CXFS (Cluster XFS) (`Cxfs`)

SGI CXFS (Cluster XFS) — R/O via XFS reader delegation. On-disk format is XFS-compatible (same 'XFSB' magic, same dinode/dir2/dir3 layout); cluster metadata in sb_features2 is intentionally ignored. Extension-only detection avoids XFS first-match collision.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.cxfs` |
| Recognised extensions | `.cxfs` |

## Detection

No byte signature: this format is recognised by its extension and by the
reader accepting the volume's own structures.

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | no | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | no | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

It does not.

## How a volume is laid out

### CxfsFormatDescriptor

R/O descriptor for SGI CXFS (Cluster XFS) volume images. Because the on-disk format is XFS-compatible (same `"XFSB"` superblock magic, same `dinode` / dir2 / dir3 layout), the reader delegates content extraction to `XfsReader` and surfaces the underlying file tree. CXFS-specific cluster metadata (sb_features2 flags, cluster UUIDs, distributed-lock bookkeeping) is intentionally ignored — those are the CMS / dmF / RGM layers, not file content. If the XFS reader cannot walk the image the descriptor falls back to a Stage-0 `metadata.ini` + `cxfs-volume.bin` surface so the volume is still identifiable.

Extension-only detection (.cxfs) avoids first-match collision with the vanilla FileSystem.Xfs descriptor — both share the same magic bytes.

References:

### CxfsReader

R/O reader for SGI CXFS (Cluster XFS) volume images via delegation to `XfsReader`.

CXFS is SGI's clustered extension of XFS. The on-disk format is XFS-compatible — same "XFSB" superblock magic at offset 0, same xfs_dsb layout, same dinode (IN magic) layout, and same dir2/dir3 directory block formats. CXFS-specific bits live in sb_features2 (offset 0x82) and in cluster-tracking fields that the lock-managing layer (CMS / dmF) consults at mount time; they do not modify the file/directory on-disk structures.

Because of that, a CXFS DAT image whose XFS layer is well-formed is readable by the vanilla XFS reader. This reader first tries the XFS reader; on success it surfaces the underlying XFS entries to the caller (cluster metadata is intentionally ignored — that is the distributed-lock / quorum / RGM layer, not file content). On failure it falls back to the Stage-0 metadata.ini + cxfs-volume.bin surface so the descriptor still identifies the image.

Honest caveat: real CXFS production volumes may use SGI-private fork formats for cluster-quota and DMAPI metadata that the open-source XFS reader does not understand; such inodes will simply be skipped by the XFS reader (it ignores unknown di_format values), and any data lurking in CXFS-only metadata regions will not be surfaced. Plain file content stored as XFS extents / inline data IS readable.

## Storage methods

- `stored` — Stored

## Further reading

- SGI "CXFS Administration Guide" (SGI techpubs) — the vendor documentation of the cluster layer
- https://mirrors.edge.kernel.org/pub/linux/utils/fs/xfs/docs/xfs_filesystem_structure.pdf — "XFS Algorithms & Data Structures", the on-disk spec CXFS volumes follow
- https://en.wikipedia.org/wiki/CXFS — Wikipedia overview

