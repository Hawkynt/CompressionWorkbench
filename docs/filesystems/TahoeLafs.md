# Tahoe-LAFS share (`TahoeLafs`)

Tahoe-LAFS share bucket — capability-encrypted Reed-Solomon share, surfaced opaque.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.tahoe-share` |
| Recognised extensions | `.tahoe-share`, `.share` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `00 00 00 01` | 0 | 0.55 |
| `00 00 00 02` | 0 | 0.55 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | no | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### TahoeLafsFormatDescriptor

Read-only descriptor for Tahoe-LAFS share buckets — single on-disk share files emitted by a Tahoe-LAFS storage server. Each share holds capability-encrypted ciphertext (one of N Reed-Solomon shares; K needed to reconstruct). Detection by the 4-byte big-endian version prefix at offset 0 (0x00000001 immutable, 0x00000002 mutable). The share payload is surfaced as a single opaque ciphertext entry — decryption requires the read-cap and is out of scope. References:

### TahoeLafsReader

Reads Tahoe-LAFS share-bucket files. Tahoe-LAFS is a distributed least- authority file system: each upload is erasure-coded into N Reed-Solomon shares, of which K are needed to reconstruct the plaintext. A single share file (typically named after a base32 share identifier and stored on disk by a "storage server") is a well-defined on-disk container — THIS is what we recognise. The container itself is opaque (capability- encrypted ciphertext) without the read-cap, so the contained share data is surfaced as a single opaque entry alongside the parsed header. Share-v1 / share-v2 header layout (big-endian, 32-bit fields at the start of the share bucket file): 0x00 u32 version (1 == immutable share v1, 2 == mutable v2) 0x04 u32 data-size (length of contained ciphertext payload) 0x08 u32 lease-count (number of leases following the data) 0x0C ... share-data-block (capability-encrypted ciphertext) Mutable (v2) buckets add a sequence number + root-hash block — we parse only the leading fields to confirm format and report metadata.

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/tahoe-lafs/tahoe-lafs — canonical implementation — share-file layout lives in the source docs
- https://tahoe-lafs.org/ — project home
- https://en.wikipedia.org/wiki/Tahoe-LAFS — Wikipedia article

