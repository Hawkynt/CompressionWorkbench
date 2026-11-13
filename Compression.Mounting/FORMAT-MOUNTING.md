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

WORM-grade creators (`IArchiveCreatable`) are sufficient to qualify for a
**staged writable mount**. The mutable namespace handles positional writes,
stable identities, renames and deletes; Flush rebuilds into a temporary file,
verifies every emitted entry's bytes, and publishes with rollback on recoverable
I/O failure. Thus no archive writer is invoked for individual kernel writes.

The current in-memory staging implementation caps the sum of original entry
bytes at 256 MiB and rejects encrypted entries, symlinks and unsafe paths. It
does not guarantee preservation of metadata that `ArchiveEntryInfo` cannot
represent. `WholeImageRebuildCommitter` is rollback-safe within a running
process but is **not power-loss atomic** for the path-backed source. For larger
archives, move the staged nodes to temporary file-backed content and publish
with a same-filesystem atomic replacement where available.

A descriptor with an actual native in-place writer may be optimized later,
but the existing `IArchiveModifiable` alone does not prove safe open-handle or
random-access semantics. For now it also uses the staged verified rebuild path
if `IArchiveCreatable` is available.

The mount backends and resolver must never invoke Windows virtual-disk mounts
or host filesystem mounting to interpret unknown content.
