# Compression.Lib

Umbrella library over every `FileFormat.*`, `FileSystem.*` and `Codec.*` project,
providing one API for detection, archive and image operations, maintenance verbs
and conversion.

`FileFormat.*` and `Codec.*` projects are referenced by name;
`FileSystems/FileSystem.*` is picked up through an exhaustive glob, so a new
filesystem joins the build without a csproj edit. Registration is emitted by the
Roslyn source generator in `Compression.Registry.Generator` — there is no
reflection and no hand-maintained list.

## Key Components

| Type | Description |
|------|-------------|
| `FormatDetector` | Identifies formats by extension and magic bytes, with dedicated PE/SFX, LZMA and tar-at-257 handling |
| `FormatRegistration` | Source-generated registration of every descriptor, building block and filesystem driver sidecar |
| `ArchiveOperations` | Unified List/Extract/Create/Convert/Resize dispatched through `FormatRegistry` |
| `ArchiveReader` / `ArchiveWriter` | Streaming read and write APIs with bounded per-entry buffering |
| `ArchiveEntry` / `ArchiveInput` / `ArchiveTestResult` | Normalized entry record, resolved create-input, integrity-check outcome |
| `CompressionOptions` | Level, method, password and threading bundle threaded through Create/Convert |
| `MethodSpec` | Parses `deflate`, `deflate+`, `lzma+` — the `+` suffix selects the best decoder-compatible encoder |
| `CompressionOptimizer` | Budgeted coordinate-descent parameter search over a single compressed stream |
| `ArchiveCompressionOptimizer` | The same search for multi-entry containers (method, level, dictionary, solid block) |
| `CvfOptimizer` | Picks the highest-effort CVF method, leaving the per-cluster stored fallback to cover the rest |
| `CompactOperation` | The `compact` composite: defragment, then optimize, then shrink |
| `ReconfigureOperation` | Geometry change with contents preserved; the result need not be smaller |
| `DeduplicationScanner` | Duplicate-file scan with a keep-which policy |
| `PartitionOperations` | MBR/GPT edits over raw or virtual-disk hosts |
| `NestedStreamResolver` | Writable stream to the deepest filesystem inside nested containers (VHD → partition → NTFS) |
| `SparseConverter` | Sparsify and densify VHD, QCOW2, VDI and VMDK |
| `InMemoryProcessing` | Threshold-gated in-memory rebuild path, committed atomically |
| `AtomicFileWriter` | Temp-file-plus-rename commit helper |
| `SfxBuilder` | Self-extracting archive builder: `[stub][archive][int64 offset]["SFX!"]` |
| `PeOverlay` | Locates the post-last-section overlay in a PE, for third-party SFX detection |
| `ExecutablePackerHandlers` | Handler lookup and detection result for packed executables |
| `AudioConversionOperation` | Capability-driven audio conversion: passthrough → remux → PCM transcode → legacy bridge |
| `SubStream` / `WritableSubStream` | Read-only and read-write sub-range stream views |
| `Layout/LayoutProfileStore` | Discovers built-in layout profiles under `templates/` and user profiles under `%APPDATA%` |
| `Layout/LayoutProfileEditorState` | Dirty-tracking and validation model behind the profile editor |
| `FsConversion/MigrationConverter` | Crash-resumable cross-filesystem file migration (`Run` / `Resume`) |
| `FsConversion/ConversionManifest` | On-disk per-file migration journal |
| `FsConversion/FilesystemResizer` | Front door for in-place resize; dispatches to `FatResizer` / `ExtResizer` |
| `FsConversion/InPlaceConverter` | In-place filesystem conversion entry point |

Chi-square incompressibility detection lives one layer down, in
`Compression.Registry.EntropyDetector`, and is consumed here by
`ArchiveOperations`.

## 3-Tier Conversion Model

| Tier | Strategy | Example |
|------|----------|---------|
| 1 | Bitstream transfer (zero decompression) | `gz` <-> `zlib`, `zip` <-> `gz` |
| 2 | Container restream (decompress wrapper only) | `tar.gz` -> `tar.xz` |
| 3 | Full recompress (extract + re-encode) | `zip` -> `7z` |

## What is covered

Per-format support is a ledger, not prose, and each ledger lives in the package
that ships the code:

- archives, compression streams and containers —
  [`Hawkynt.FileFormats.Archives/README.md`](../Hawkynt.FileFormats.Archives/README.md)
- filesystems and disk images —
  [`Hawkynt.FileFormats.FileSystems/README.md`](../Hawkynt.FileFormats.FileSystems/README.md)
- audio codecs and containers —
  [`Hawkynt.FileFormats.Audio/README.md`](../Hawkynt.FileFormats.Audio/README.md)

The verbs those tables mark, and the interface a descriptor has to implement to
unlock each one, are defined in [`docs/ARCHIVE-MODEL.md`](../docs/ARCHIVE-MODEL.md).

## Visibility

All API types are `public`. `InternalsVisibleTo` is only used for `Compression.Tests`.
