# Mounting arbitrary registered formats

The Windows Dokan and Linux FUSE adapters consume the same `IFilesystemSession`.
Neither adapter parses ZIP, NTFS, VMDK or other formats. The
`MountNamespaceResolver` chooses a userspace source projection first.

| Source | Namespace | Read/write policy |
| --- | --- | --- |
| Native filesystem (FAT, NTFS, ext, Btrfs, ISO, etc.) | Native `IFilesystemDriverProvider` or registered sidecar; otherwise the existing archive-derived namespace | R/W **only** if the exact media profile, backing stream and backend qualify for the complete mount-grade write contract |
| ZIP, 7z, RAR and other archive formats | Hierarchical `List/OpenEntry` read view; staged mutable session when `IArchiveCreatable` qualifies | R/W via temporary whole-archive rebuild and candidate verification on Flush; without a safe writer, R/O |
| VMDK, VHD, QCOW2 and other block containers with an `IRandomAccessBlockDeviceProvider` | Decode the virtual block device, detect partitions and delegate to the registered filesystem drivers | Single recognized filesystem: its exact qualified access; composite partition root: R/O |
| Raw sector-addressable files (`.img`, `.ima`, `.dd`, `.raw`) without a more specific detected format | 512-byte `StreamBlockDevice`, partition detection and registered filesystem drivers | Exact guest profile; composite partition root R/O |
| Structured disk/media containers (including formats without a sector-addressable provider) | Their registered archive projection, if available | R/O unless an exact native mount-grade session exists |
| Registered standalone media, executable, image or stream format with no archive namespace | One file holding **the original source bytes**, retaining the source filename | R/O only; no invented decoded frames/tracks/resources |
| Unknown or unregistered format | No automatic mount | Unsupported rather than guessing the format |

Raw PC disk-image fallback is deliberately limited to sector-aligned 512-byte images.
It rejects unrecognized content; it does not guess that a `.d90` or a random
media `.img` contains an MS-DOS partition table.

The raw single-file projection is a **byte-preserving fallback**, not a
filesystem parser and not a media decoder. It does not claim that a particular
compressed stream has been expanded. A format with a genuine list/open-entry
surface takes precedence.

For multi-file images (such as some CloneCD layouts), selecting just the
primary file is not sufficient to reconstruct companion files. A native
multi-stream mount path must bind and validate the complete source set. The
single-file fallback never claims multi-file image reconstruction.

WORM-grade creators (`IArchiveCreatable`) can qualify for a
**staged writable mount** after the resolver checks the archive category,
safe ordinary entries and a lossless reconstruction of creator-only sources.
The mutable namespace handles positional writes,
stable identities, renames and deletes; Flush rebuilds into a temporary file,
verifies every emitted entry's bytes, and publishes with rollback on recoverable
I/O failure. Thus no archive writer is invoked for individual kernel writes.

Archive file content is staged in separate temporary files. Positional reads,
writes, sparse extends and truncations use 64-bit file offsets rather than
`byte[]` allocation, including for entries larger than 2 GiB. Writers are
invoked through `IArchiveCreatable.CreateFromStreams`. A format's *default*
implementation of that method still buffers an entire entry, so true bounded
memory also requires its descriptor to override the streaming writer (ZIP,
7z and RAR provide implementations). Encrypted/linked/unsafe entries are rejected.

For path-backed staged mounts, **ZIP's native delta path** uses its existing
`ZipModifier.Add/Remove` on a private candidate; unchanged ZIP records and
compressed content are retained. Edits for all other formats currently use
a complete streamed candidate rebuild. This path publishes a verified replacement
instead of patching the live archive.

Generic stream mounts retain the separately qualified editor path. A named
writable ZIP `FileStream` can opt into its native in-place writer under a flushed,
checksum-verified undo journal. Failed writes or validation roll back; a surviving
undo journal is recovered before mounting. Other streams use private candidates.
The resolver retains conservative large-member limits for formats without a
qualified large streaming profile; disk-backed staging alone does not remove a
descriptor's serializer limits. See [format-family qualification](../docs/MOUNTING-FORMAT-FAMILIES.md).

For a mounted **local path**, Flush creates a candidate and a verified backup
*in the archive's directory*, flushes both to disk, then writes a durable
journal containing old/new SHA-256 hashes and candidate/backup names. Only
after exact entry-content validation does it replace the archive pathname.
Next mount holds an exclusive `.cwbmount.lock`, reads a surviving journal,
accepts either verified version, or restores the original from the verified
backup when neither matches. Other changes to the source since mount are
detected by SHA-256 and rejected. Failed publication preserves staging files
for manual recovery. The journal and backup are removed after successful
publication/recovery.

**Durability limit:** managed `.NET` can flush the candidate, backup, and
journal files; it does not expose portable parent-directory `fsync`. Therefore
neither power-loss durability on every filesystem nor crash-atomic rename on
every Windows/filesystem configuration can be promised solely from `File.Move`.
Do not claim strict power-loss atomicity until each platform supplies a
verified durable directory-metadata barrier. The explicit staged-path API and
qualified stream API use their respective recovery journals; neither promises
portable power-loss atomicity.

No arbitrary `IArchiveModifiable` is treated as a safe native mount-grade
writer: ZIP is explicitly qualified; other editors need per-format tests.

The mount backends and resolver must never invoke Windows virtual-disk mounts
or host filesystem mounting to interpret unknown content.
