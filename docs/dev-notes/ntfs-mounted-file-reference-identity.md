# NTFS mounted file-reference identity

The native mounted NTFS sidecar treats an NTFS file reference as the pair recorded by the on-disk format: the low 48-bit MFT segment number plus the 16-bit FILE-record sequence number. `FilesystemNodeId.Value` carries the MFT segment number and `FilesystemNodeId.Generation` carries that sequence.

The sequence number is not cosmetic metadata. Reusing an MFT segment advances its sequence, so a directory or attribute reference carrying an older sequence is stale and must not resolve to the new object occupying that segment. The mounted probe therefore validates the selected `$FILE_NAME` parent reference against the live parent FILE record before publishing the namespace.

The identity scanner follows the unnamed `$MFT::$DATA` mapping pairs instead of assuming the MFT is physically contiguous. FILE-record Update Sequence Arrays are validated using the bytes-per-sector value declared by the volume boot sector.

## References

- Microsoft `FILE_NAME` structure documentation: one file-name attribute exists for every directory entry and `ParentDirectory` is a file reference.
- Microsoft / Windows NTFS file-record structures: a FILE record contains its sequence number and hard-link count.
- Linux `fs/ntfs3/ntfs.h` was consulted only as an independent on-disk-layout oracle. It is GPL-2.0; no implementation code, comments, naming or control flow were copied or translated.

## Deliberate remaining boundary

The general `NtfsReader` still projects one preferred `$FILE_NAME` per MFT record. Complete mounted hard-link enumeration must be reconstructed from validated `$I30` (`$INDEX_ROOT` / `$INDEX_ALLOCATION` plus bitmap) directory entries, not merely from every `$FILE_NAME` attribute: publishing an attribute that is no longer indexed by its parent would expose an orphan/stale alias. Writable mounting therefore remains disabled.
