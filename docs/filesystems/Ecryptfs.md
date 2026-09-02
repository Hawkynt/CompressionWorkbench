# eCryptfs (`Ecryptfs`)

eCryptfs file-level encryption container — header surface + opaque ciphertext.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.ecryptfs` |
| Recognised extensions | `.ecryptfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `3C 81 B7 F5` | 0 | 0.95 |

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

### EcryptfsFormatDescriptor

Read-only descriptor for eCryptfs per-file encryption containers. eCryptfs (Linux) stacks on top of any underlying FS and stores each encrypted file with a 4-byte big-endian marker `0x3C81B7F5` at offset 0 followed by an 8-byte decrypted size, 4-byte flags, and 4-byte extent-size hint. Decryption requires the user's passphrase + EFEK packets — out of scope. The encrypted payload is surfaced as a single opaque entry along with the parsed header metadata. References:

### EcryptfsReader

Reads eCryptfs per-file encryption headers. eCryptfs is a stacking file-level encryption filesystem (Linux) — every encrypted file is stored on the lower filesystem as a regular file whose first page is a metadata header followed by AES-CBC ciphertext extents. The header starts with a 4-byte big-endian marker (`0x3C81B7F5`) so the individual on-disk container is well-defined and detectable. Decryption requires the user's mount passphrase + EFEK (Encrypted File Encryption Key) tag-3 / tag-11 packets and is OUT OF SCOPE; this reader surfaces the parsed header + the encrypted payload as a single opaque entry. File header layout (big-endian, file offset 0): 0x00 u32 marker == 0x3C81B7F5 0x04 u64 decrypted-size (plaintext length, host-endian on Linux) 0x0C u32 flags 0x10 u32 extent-size (typically 4096) 0x14 ... EFEK packets, tag-3 / tag-11 OpenPGP-style ~0x800 start of AES-CBC ciphertext extents

## Storage methods

- `stored` — Stored

## Further reading

- https://docs.kernel.org/filesystems/ecryptfs.html — Linux kernel eCryptfs documentation
- https://github.com/torvalds/linux/tree/master/fs/ecryptfs — mainline implementation (ecryptfs_kernel.h defines the file-header marker + packet layout)
- https://en.wikipedia.org/wiki/ECryptfs — Wikipedia overview

