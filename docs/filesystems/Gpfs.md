# IBM Spectrum Scale / GPFS (`Gpfs`)

IBM Spectrum Scale / GPFS — Stage-0 detection-only — proprietary IBM on-disk format; magic 0x4347465C at offset 0 of NSD descriptor. Promotion to R/O deferred: full inode/directory/allocation layout not publicly specified, file table lives in cluster manager across multiple NSDs (no single-image surface), and no fsck-equivalent oracle exists off-cluster. See descriptor source comment for the full deferral rationale.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.gpfs` |
| Recognised extensions | `.gpfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `43 47 46 5C` | 0 | 0.90 |

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

### GpfsFormatDescriptor

Stage 0 detection-only descriptor for IBM Spectrum Scale (GPFS) NSD descriptor images. Surfaces only a synthetic `metadata.ini` and the raw image bytes; no real file-walk is attempted. References:

### GpfsReader

Stage 0 detection-only reader for IBM Spectrum Scale (formerly GPFS — General Parallel File System) NSD (Network Shared Disk) descriptor images. GPFS is a parallel clustered FS — its single-disk surface is the NSD descriptor block whose first four bytes are the GPFS magic integer `0x4347465C` (the bytes 0x43 0x47 0x46 0x5C — derived from the cluster signature "GCFS\" used in GPFS internal headers). Only the magic word is verified. The real NSD descriptor maps onto a GPFS cluster's failure-group topology and storage pool membership; the file table itself lives in the cluster manager and cannot be walked from a single disk image.

## Storage methods

- `stored` — Stored

## Further reading

- https://www.ibm.com/docs/en/storage-scale — IBM Storage Scale (formerly Spectrum Scale / GPFS) official documentation, incl. NSD concepts
- https://en.wikipedia.org/wiki/GPFS — Wikipedia overview

