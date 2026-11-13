# Mounting arbitrary registered formats

The Windows Dokan and Linux FUSE adapters consume the same `IFilesystemSession`.
Neither adapter parses ZIP, NTFS, VMDK or other formats. The
`MountNamespaceResolver` chooses a userspace source projection first.

| Source | Namespace | Read/write policy |
| --- | --- | --- |
| Native filesystem (FAT, NTFS, ext, Btrfs, ISO, etc.) | Native `IFilesystemDriverProvider` or registered sidecar; otherwise the existing archive-derived namespace | R/W **only** if the exact media profile, backing stream and backend qualify for the complete mount-grade write contract |
| ZIP, 7z, RAR and other archive formats | Hierarchical read-only `IArchiveFormatOperations.List/OpenEntry` projection | R/O; `IArchiveModifiable` is not a promise of random-write files, durable rename, or open-handle semantics |
| VMDK, VHD, QCOW2 and other block containers with an `IRandomAccessBlockDeviceProvider` | Decode the virtual block device, detect partitions and delegate to the registered filesystem drivers | Single recognized filesystem: its exact qualified access; composite partition root: R/O |
| Raw sector-addressable files (`.img`, `.ima`, `.dd`, `.raw`) without a more specific detected format | 512-byte `StreamBlockDevice`, partition detection and registered filesystem drivers | Exact guest profile; composite partition root R/O |\n| Structured disk/media containers (including formats without a sector-addressable provider) | Their registered archive projection, if available | R/O unless an exact native mount-grade session exists |
| Registered standalone media, executable, image or stream format with no archive namespace | One file holding **the original source bytes**, retaining the source filename | R/O only; no invented decoded frames/tracks/resources |
| Unknown or unregistered format | No automatic mount | Unsupported rather than guessing the format |

Raw PC disk-image fallback is deliberately limited to sector-aligned 512-byte images.\nIt rejects unrecognized content; it does not guess that a `.d90` or a random\nmedia `.img` contains an MS-DOS partition table.\n\nThe raw single-file projection is a **byte-preserving fallback**, not a
filesystem parser and not a media decoder. It does not claim that a particular
compressed stream has been expanded. A format with a genuine list/open-entry
surface takes precedence.

For multi-file images (such as some CloneCD layouts), selecting just the
primary file is not sufficient to reconstruct companion files. A native
multi-stream mount path must bind and validate the complete source set. The
single-file fallback never claims multi-file image reconstruction.

Per-source safety is intentional. A writable ZIP, 7z or RAR archive API
cannot simply be mapped to Windows `WriteFile`: open handles, partial writes,
flushes, replacements, concurrent access and crash-safe publication need a
separate transactional writable archive-session model. No R/W flag is shown
until such a model exists and has conformance tests.

The mount backends and resolver must never invoke Windows virtual-disk mounts
or host filesystem mounting to interpret unknown content.
