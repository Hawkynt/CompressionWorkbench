# CompressionWorkbench

[![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
[![Language](https://img.shields.io/github/languages/top/Hawkynt/CompressionWorkbench?color=8957D5)](https://github.com/Hawkynt/CompressionWorkbench)

[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/CompressionWorkbench?branch=main)
![Activity](https://img.shields.io/github/commit-activity/m/Hawkynt/CompressionWorkbench)

[![Stars](https://img.shields.io/github/stars/Hawkynt/CompressionWorkbench?color=FFD700)](https://github.com/Hawkynt/CompressionWorkbench/stargazers)
[![Forks](https://img.shields.io/github/forks/Hawkynt/CompressionWorkbench?color=008080)](https://github.com/Hawkynt/CompressionWorkbench/network/members)
[![Issues](https://img.shields.io/github/issues/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/issues)
![Code Size](https://img.shields.io/github/languages/code-size/Hawkynt/CompressionWorkbench?color=4CAF50)
![Repo Size](https://img.shields.io/github/repo-size/Hawkynt/CompressionWorkbench?color=FF9800)

[![Release](https://img.shields.io/github/v/release/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/releases/latest)
[![Nightly](https://img.shields.io/github/v/release/Hawkynt/CompressionWorkbench?include_prereleases&sort=date&filter=nightly-*&label=nightly&color=FF9800)](https://github.com/Hawkynt/CompressionWorkbench/releases)
[![Downloads](https://img.shields.io/github/downloads/Hawkynt/CompressionWorkbench/total)](https://github.com/Hawkynt/CompressionWorkbench/releases)
[![NuGet Core](https://img.shields.io/nuget/v/Hawkynt.Compression.Core?label=Core)](https://www.nuget.org/packages/Hawkynt.Compression.Core/)
[![NuGet Audio](https://img.shields.io/nuget/v/Hawkynt.FileFormats.Audio?label=Audio)](https://www.nuget.org/packages/Hawkynt.FileFormats.Audio/)
[![NuGet Archives](https://img.shields.io/nuget/v/Hawkynt.FileFormats.Archives?label=Archives)](https://www.nuget.org/packages/Hawkynt.FileFormats.Archives/)
[![NuGet FileSystems](https://img.shields.io/nuget/v/Hawkynt.FileFormats.FileSystems?label=FileSystems)](https://www.nuget.org/packages/Hawkynt.FileFormats.FileSystems/)

> A fully clean-room C# implementation of compression primitives, archive file formats, and analysis tools. Every algorithm is implemented from scratch using no external compression source code — only our own primitives.

---

CompressionWorkbench is built on a single, ambitious premise: **if you have this
software, you should never need another archiver again.** Every algorithm, every
archive format, every filesystem, every file that is secretly an archive, and every
parameter combination — no matter how exotic — supported to the fullest state each
format allows.

The foundation is a library of **primitive building blocks**: the raw compression
algorithms (dictionary coders, entropy coders, transforms, filters) implemented as
composable, standalone primitives. Everything else is assembled from these blocks
rather than reimplemented. On top of them we chase **each and every exotic compression
format** ever shipped — mainstream, obscure, retro, and long-forgotten alike — exposed
through a **universal compressor** that surfaces the *complete* option set of each format
instead of a lowest-common-denominator subset, paired with a **compression optimizer**
that hunts for the parameter combination yielding the best result on your actual data.

We treat **everything as a potentially-addressable archive** — the
"everything-may-be-an-archive" principle: not just ZIP/7z/RAR, but every container,
document, package, disk image, and file format that holds multiple payloads inside it.
That reaches all the way down to **filesystems**: the goal is to get every filesystem in,
and to make each one **read *and* write** — with a **WORM** (write-once) path as the
honest intermediate state on the way to full read/write. Each writable filesystem gets a
**layout optimizer** (cluster / block / MFT sizing tuned to minimise wasted space),
**defragmentation**, and a relentless **hunt for exotic parameter combinations** that
turns partial support into complete support for every variant out there.

The end state: every codec, every archive format, every filesystem, every pseudo-archive,
and every exotic parameter combination — handled in one place, as completely as each
format permits. One tool, for all of it.

---

## 📦 Quick start

**Install via NuGet** — pick the surface you need:

```bash
dotnet add package Hawkynt.Compression.Core         # primitives only
dotnet add package Hawkynt.FileFormats.Audio        # + audio codecs / containers
dotnet add package Hawkynt.FileFormats.Archives     # + zip / tar / 7z / and the long tail
dotnet add package Hawkynt.FileFormats.FileSystems  # + FAT / ext / NTFS / VHD / VMDK / etc.
```

**Use the CLI** — `cwb` is a self-contained single-file executable, no .NET runtime needed:

```bash
cwb list mystery.bin            # auto-detect format and list contents
cwb extract photos.tar.gz       # auto-detect compression chain + extract
cwb analyze unknown.bin         # entropy heatmap + signature scan + trial decompression
cwb benchmark sample.txt        # compare every building block on your data
cwb auto-extract sample.vhd --recursive  # disk → partition → filesystem → file
```

**Read the per-package format reference** to find out what's actually supported, audited
against the real source code (R / WORM / R/W states, upstream spec links, limitations):
[Archives](Hawkynt.FileFormats.Archives/README.md) ·
[Audio](Hawkynt.FileFormats.Audio/README.md) ·
[FileSystems](Hawkynt.FileFormats.FileSystems/README.md) ·
[Building blocks](Compression.Core/README.md).

---

## Vision

CompressionWorkbench exists to answer two kinds of questions about compressed and packaged data, entirely in managed .NET with no native dependency on zlib, liblzma, libarchive, or any other third-party compression library:

1. **"What is this, and what is inside?"** — given an arbitrary blob of bytes, identify the format, slice it into its logical payloads, and recover the original data.
2. **"How does the algorithm work, and how does it compare?"** — provide a reference implementation of every major compression primitive, from LZ77 through arithmetic coding to modern neural / context-mixing compressors, so the algorithms can be read, benchmarked, and taught from a single codebase.

Concretely that means:

- **Clean-room, from-scratch C#.** Every primitive — bit I/O, Huffman, range coding, LZ family, BWT/MTF, PPM, context mixing, modern ANS/FSE — is written from the original specification or from a clean reverse of the reference algorithm. No line of native compression code is linked in or ported.
- **Every common container, read *and* written** wherever a spec exists to write against honestly. When the writer cannot match an external spec (proprietary element streams, missing on-disk structures), that is documented in the support tables instead of shipping a silent toy.
- **Every multi-payload container treated as an archive.** The distinction that matters to a user is "can I list and extract the N things inside?", not "is this called ZIP". That makes PE resource DLLs, multi-page TIFFs, font collections, multi-frame GIFs, PSD layer stacks, and MPEG transport streams all first-class archives — see [Archives and Pseudo-archives](#archives-and-pseudo-archives) below.
- **Analysis as a first-class surface.** Identification, entropy mapping, trial decompression, chain reconstruction, signature scanning, and cross-validation against external tools are exposed through a library (`Compression.Analysis`), a CLI (`cwb`), and a UI visualiser — not as an afterthought.
- **Benchmarking at the primitive level.** The benchmark compares the building blocks — raw algorithms without container overhead — so ratio/speed numbers reflect the algorithm, not the envelope.
- **One library, many surfaces.** CLI archiver (`cwb`), UI browser + analyser, Explorer shell integration, self-extracting stubs (`Compression.Sfx.*`), and a library any .NET consumer can link.

---

## Archives and Pseudo-archives

> **Any format that packages N discrete, separately-addressable payloads is an archive.**

A format earns archive treatment — the `IArchiveFormatOperations` contract (`List` / `Extract` / optional `Create`) — whenever its binary layout contains:

1. A directory or index of named or indexed entries, **and**
2. Each entry can be extracted as an independent blob, **and**
3. A consumer might plausibly want one entry without the others.

This is true regardless of whether the entries happen to be files, images, pages, frames, tracks, layers, tables, fonts, strings, or other domain objects. The contents of an extracted blob remain domain-specific (a TIFF page is still a TIFF, an `RT_ICON` resource is still an icon), but that is a property of the payload, not of the container.

### Real archives

Formats in the canonical archive sense — ZIP, TAR, 7z, RAR, CAB, CPIO, and their relatives.
They were designed as "a bag of files with a directory". The exhaustive per-format reference
(extensions, R / WORM / R/W state, upstream spec link, limitations) is in
**[Hawkynt.FileFormats.Archives/README.md](Hawkynt.FileFormats.Archives/README.md)**.

### Pseudo-archives

Formats that *are* archives by structure but have never been presented that way in ordinary file managers. CompressionWorkbench slices each one along its natural payload boundary and exposes the same `List` / `Extract` surface as ZIP.

State columns audited against actual `IArchiveCreatable` / `IArchiveModifiable` implementation, not advertised intent. Where one bullet covers multiple projects with different states (e.g. ICO is WORM, ANI is R), each project's state is shown explicitly.

| Container                              | State                                  | Entries become                                                                     | Where shipped                                          |
| -------------------------------------- | -------------------------------------- | ---------------------------------------------------------------------------------- | ------------------------------------------------------ |
| **PE resource DLLs/EXEs**              | PeResources=R, ResourceDll=WORM        | one entry per resource: `RT_GROUP_ICON` → `.ico`, `RT_BITMAP` → `.bmp`, `RT_MANIFEST` → `.xml`, `RT_STRING` → `.txt`, `RT_VERSION` → `.rcv`, raw `RT_RCDATA` | `FileFormat.PeResources`, `FileFormat.ResourceDll`     |
| **ICO / CUR / ANI**                    | Ico=WORM, Ani=R                        | one entry per `ICONDIRENTRY` → `.png` / `.bmp` (cursor adds hotspot)               | `FileFormat.Ico`, `FileFormat.PngCrushAdapters.Ani`    |
| **Multi-page TIFF / BigTIFF**          | sibling-provided                       | one single-page `.tif` per IFD                                                     | `FileFormat.PngCrushAdapters.Tiff` / `BigTiff`         |
| **Multi-frame GIF / MNG / FLI / DCX**  | sibling-provided                       | one `.gif` / `.png` per frame                                                      | `FileFormat.Gif`, `PngCrushAdapters.{Mng,Fli,Dcx}`     |
| **Animated PNG (APNG)**                | sibling-provided                       | one `.png` per frame with dispose/blend applied against previous frames            | `FileFormat.PngCrushAdapters.Apng`                     |
| **Icon containers (ICNS, MPO)**        | sibling-provided                       | Apple icon suite / stereoscopic JPEG pair                                          | `FileFormat.PngCrushAdapters.{Icns,Mpo}`               |
| **Font collections (TTC / OTC)**       | R                                      | one `.ttf` / `.otf` per member font                                                | `FileFormat.FontCollection`                            |
| **Single-font (TTF / OTF)**            | R                                      | per-glyph entries (cmap + glyf slicing; CFF/OpenType passes through)               | `FileFormat.FontCollection.Ttf`                        |
| **Gettext MO / PO**                    | R                                      | one `.txt` per msgid/msgstr pair                                                   | `FileFormat.Gettext`                                   |
| **WAV / FLAC / MP3**                   | WAV=WORM, FLAC=WORM, MP3=WORM          | full file + per-channel WAV + ID3v2/RIFF metadata + APIC cover art                 | `FileFormat.Wav`, `FileFormat.Flac`, `FileFormat.Mp3`  |
| **Ogg**                                | R                                      | per-logical-stream packets + Vorbis/Opus comments                                  | `FileFormat.Ogg`                                       |
| **MP4 / MOV / MKV / WebM**             | R                                      | demuxed tracks (H.264 → Annex-B), attachments, chapters                            | `FileFormat.Mp4`, `FileFormat.Matroska`                |
| **MPEG Transport Stream**              | R                                      | per-PID elementary streams (video/audio/data)                                      | `FileFormat.MpegTs`                                    |
| **Blu-ray PGS (SUP)**                  | R                                      | subtitle segments grouped by epoch                                                 | `FileFormat.Sup`                                       |
| **VobSub (DVD)**                       | R                                      | `.idx` metadata + per-entry slices of the sibling `.sub` PES stream                | `FileFormat.VobSub`                                    |
| **HLS M3U8**                           | R                                      | segment list with per-variant metadata                                             | `FileFormat.M3u8`                                      |
| **U-Boot uImage, FDT/DTB, UEFI FV**    | R                                      | firmware header metadata + decompressed payload or per-FFS/property entries        | `FileFormat.UImage`, `FileFormat.Dtb`, `FileFormat.UefiFv` |
| **Device executable packers**          | R                                      | the packer's `metadata.ini` (detection evidence) + `packed_payload.bin` (or in-process decompressed body for UPX) | `FileFormat.ExePackers`                                |

### Honest failure

Formats that cannot produce multiple addressable entries stay in `FormatCategory.Stream` rather than falsely advertising themselves as archives. `IArchiveFormatOperations.List` is free to return a single "whole payload" entry for stream-style containers (and does, for formats like PAQ8 or the audio-stream-as-archive descriptors), but a format that would have to fake an index has no business claiming `SupportsMultipleEntries`.

---

## Solution Structure

The solution uses the `.slnx` XML format. Core / library / tooling projects sit at the
repository root; the individual format projects are grouped into three subdirectories
by domain. Three meta-package projects bundle them into NuGet drops.

```
CompressionWorkbench.slnx
|
+-- Compression.Core                 Primitives, building blocks, SIMD, partition parsers
+-- Compression.Registry             Interfaces (IFormatDescriptor, IBuildingBlock) + registries
+-- Compression.Registry.Generator   Roslyn source generator for auto-discovery
+-- Compression.Lib                  Umbrella library: detection, archive ops, SFX hosting
+-- Compression.Analysis             Binary analysis engine (signatures, entropy, trial decomp)
+-- Compression.CLI                  `cwb` command-line tool (System.CommandLine v3)
+-- Compression.UI                   WPF browser + analyser + heatmap + wizard
+-- Compression.Shell                Explorer context-menu integration
+-- Compression.Sfx.Cli              Self-extracting archive stub (console)
+-- Compression.Sfx.Ui               Self-extracting archive stub (GUI)
+-- Compression.Tests                NUnit test project
|
+-- Hawkynt.FileFormats.Audio/       Meta-package: bundles every Codec.* + audio FileFormat.*
+-- Hawkynt.FileFormats.Archives/    Meta-package: every archive / compression-stream / pseudo-archive
+-- Hawkynt.FileFormats.FileSystems/ Meta-package: every filesystem + disk-image container
|
+-- Codecs/Codec.*/                  Standalone audio codecs (PCM / FLAC / A-law / μ-law / GSM /
|                                    ADPCM / MIDI / MP3 / Vorbis / Opus / AAC)
+-- FileFormats/FileFormat.*/        One project per archive / stream / pseudo-archive / packer
+-- FileSystems/FileSystem.*/        One project per filesystem image format
```

Adding a new format is a four-step process:

1. Create the project under the right bucket: `FileFormats/FileFormat.<Name>/` for an
   archive / compression stream / pseudo-archive, `FileSystems/FileSystem.<Name>/` for a
   filesystem image, or `Codecs/Codec.<Name>/` for an audio codec. Add a class implementing
   `IFormatDescriptor` plus the appropriate operations interface (`IStreamFormatOperations`,
   `IArchiveFormatOperations`, plus optionally `IArchiveCreatable` for WORM, or
   `IArchiveModifiable` for full R/W).
2. Add a `<ProjectReference>` from `Compression.Lib.csproj` so the source generator picks it up.
3. Add a `<ProjectReference PrivateAssets="all" />` to the matching meta-package csproj
   (`Hawkynt.FileFormats.{Audio,Archives,FileSystems}/Hawkynt.FileFormats.*.csproj`) so the
   format ships in its NuGet meta-package and the meta README's reference table can include it.
4. Add the project to `CompressionWorkbench.slnx`.

The Roslyn source generator (`Compression.Registry.Generator`) discovers every implementation
at compile time and emits the registration table. No reflection, no hand-maintained switch
statements, no init hooks.

For more detail on conventions, testing, and the registry mechanism, see
[CONTRIBUTING.md](CONTRIBUTING.md).

### Technology stack

| Concern   | Choice                                                              |
| --------- | ------------------------------------------------------------------- |
| Language  | C# 14 / .NET 10                                                     |
| Solution  | `.slnx` (XML solution format)                                       |
| Testing   | NUnit                                                               |
| GUI       | WPF                                                                 |
| CLI       | System.CommandLine v3                                               |
| Discovery | Roslyn source generator (zero-reflection format/block registration) |
| Bundling  | Costura.Fody single-file embedding for CLI/UI/SFX                   |

---

## Supported Formats

### Capability scale

The state shown for each format in the meta-package READMEs is **audited against the actual
source code** — `IArchiveCreatable` and `IArchiveModifiable` interface implementations,
`FormatCapabilities.CanCreate` / `CanModify` flags — not advertised intent.

| State           | Meaning                                                                                  | Source-code signal                                            |
| --------------- | ---------------------------------------------------------------------------------------- | ------------------------------------------------------------- |
| **Unsupported** | No descriptor exists.                                                                    | —                                                             |
| **R**           | Read-only: can `List` / `Extract` / `Test`; no creation.                                 | `IArchiveFormatOperations` only                               |
| **WORM**        | Write-Once-Read-Many: can produce a fresh archive / image, but cannot modify in place.   | `IArchiveCreatable` (or `FormatCapabilities.CanCreate`)       |
| **R/W**         | Can also add / replace / remove entries inside an existing archive with consistent free-space bookkeeping. | `IArchiveModifiable` (or `FormatCapabilities.CanModify`)      |

### NuGet meta-packages

The raw algorithm primitives registered via `IBuildingBlock` live in `Hawkynt.Compression.Core`
and are published as the foundation NuGet package. Three sister meta-packages add format
coverage on top — all version-locked 1:1, all built from the same source repo, all
single-responsibility:

| Package | Surface | NuGet |
|---|---|---|
| `Hawkynt.Compression.Core` | Compression primitives + `IBuildingBlock` registry. PCM / WAV / archive / filesystem code lives elsewhere. | [![Core](https://img.shields.io/nuget/v/Hawkynt.Compression.Core?label=Hawkynt.Compression.Core)](https://www.nuget.org/packages/Hawkynt.Compression.Core/) |
| `Hawkynt.FileFormats.Audio` | PCM + lossy/lossless audio codecs (MP3 / AAC / Vorbis / Opus / FLAC / ALAC) + audio containers + tracker / chiptune / game-audio bundles. | [![Audio](https://img.shields.io/nuget/v/Hawkynt.FileFormats.Audio?label=Hawkynt.FileFormats.Audio)](https://www.nuget.org/packages/Hawkynt.FileFormats.Audio/) |
| `Hawkynt.FileFormats.Archives` | Catch-all for every format with multiple addressable payloads: archives (zip / tar / 7z / rar / cab / wim) + compression streams + Office / ODF / web bundles + software / installer packages + game / engine archives + pseudo-archives (PE resources, ICO, font collections, gettext) + multi-track video / subtitle containers + scientific / ML / CAD / 3D / medical containers + executable packer detection. | [![Archives](https://img.shields.io/nuget/v/Hawkynt.FileFormats.Archives?label=Hawkynt.FileFormats.Archives)](https://www.nuget.org/packages/Hawkynt.FileFormats.Archives/) |
| `Hawkynt.FileFormats.FileSystems` | FAT / exFAT / NTFS / ext / Btrfs / XFS / HFS+ / APFS / SquashFS / ISO9660 / UDF / ZFS + retro-disk formats + disk-image containers (VHD / VHDX / VMDK / VDI / QCOW2 / DMG) + firmware / embedded boot images (uImage / Device Tree Blob / UEFI FV / Intel HEX / SREC). | [![FileSystems](https://img.shields.io/nuget/v/Hawkynt.FileFormats.FileSystems?label=Hawkynt.FileFormats.FileSystems)](https://www.nuget.org/packages/Hawkynt.FileFormats.FileSystems/) |

A fifth sister package — `Hawkynt.FileFormats.Images` — lives in the
[`Hawkynt/PNGCrushCS`](https://github.com/Hawkynt/PNGCrushCS) sibling repo and supplies
image-format coverage (PNG / JPEG / TIFF / APNG / etc.). Together the five packages form one
cohesive surface without any one dragging in the others' transitive baggage.

**Where each per-format detail table lives:**

- **[Compression.Core/README.md](Compression.Core/README.md)** — every building block (Dictionary
  / Entropy / Transform / Context-Mixing families) with reference papers and known-edge-case
  notes. The canonical "how / when / why" for picking a compression primitive.
- **[Hawkynt.FileFormats.Archives/README.md](Hawkynt.FileFormats.Archives/README.md)** — the
  the archive / stream / pseudo-archive descriptors with R / WORM / R/W state per format.
- **[Hawkynt.FileFormats.Audio/README.md](Hawkynt.FileFormats.Audio/README.md)** — every codec
  + container with its production / partial / framing-only state honestly documented.
- **[Hawkynt.FileFormats.FileSystems/README.md](Hawkynt.FileFormats.FileSystems/README.md)** —
  filesystems, disk-image containers, firmware images, plus the WSL-validated external-tool
  matrix and the forensic-recovery carver API.

## Tools

CompressionWorkbench exposes the same core library through five different surfaces. Pick the one that fits the task.

### Compression.CLI — `cwb`

Universal archive tool with smart conversion, optimal re-encoding, benchmarking, and analysis built in.

| Command                        | Alias   | What it does                                                |
| ------------------------------ | ------- | ----------------------------------------------------------- |
| `list <archive>`               | `l`     | List contents of an archive                                 |
| `extract <archive> [files...]` | `x`     | Extract files from an archive                               |
| `create <archive> <files...>`  | `c`     | Create a new archive                                        |
| `test <archive>`               | `t`     | Test archive integrity                                      |
| `add <archive> <files...>`     | -       | Add or replace files inside an existing archive             |
| `remove <archive> <names...>`  | -       | Remove named entries from an existing archive               |
| `replace <archive> <entry> <file>` | -   | Replace a single entry with a new file                      |
| `info <archive>`               | -       | Show detailed archive information                           |
| `convert <input> <output>`     | -       | Convert between any formats (archive, FS, stream)           |
| `optimize <input> <output>`    | `opt`   | Re-encode with optimal compression                          |
| `benchmark <file>`             | `bench` | Benchmark all building blocks on the supplied data          |
| `formats`                      | -       | List all supported formats                                  |
| `analyze <file>`               | -       | Run binary analysis (detection + entropy + trial decompress)|
| `auto-extract <file>`          | -       | Recursive nested extraction (see below)                     |
| `batch <dir>`                  | -       | Scan a directory in parallel and aggregate format stats     |
| `suggest <file>`               | -       | Platform-aware format recommendation                        |
| `tool (init\|list\|add\|run\|remove)` | - | Manage external-tool templates                          |
| `reverse-engineer <tool>`      | `reveng`| Black-box probing of an unknown compression tool            |
| `carve <file>`                 | -       | Photorec-style file carver (JPEG/PNG/MP4/ZIP/... at any offset, including in slack space) |
| `visualize <file>`             | -       | Renders a colored block map of every detected envelope (FAT/ext/NTFS/MBR/...) stacked by depth. `--format ascii\|svg\|html` |
| `defragment <image>`           | -       | Defragment a filesystem image in place (`--mode pack-start\|pack-end\|fill-holes\|carve-hole`) |
| `shrink <image>`               | -       | Defragment + truncate trailing free space; `--compact` for VHD sparse |
| `wipe-empty <image>`           | -       | Zero-fill all unused space (free clusters, cluster tips, deleted entries) |
| `deploy <image> <device>`      | -       | Raw-write an image to a block device with CRC verification  |
| `convert-clusters <image>`     | -       | Rebuild a FAT image with a different cluster size           |
| `resize <image>`               | -       | Resize a filesystem image to a target size                  |
| `convert-archive <in> <out>`   | -       | Convert between any listable/creatable formats (archive-to-archive, archive-to-FS, FS-to-archive, FS-to-FS). `convert-fs` is a hidden back-compat alias. |
| `dedup <image>`                | -       | Find and optionally remove duplicate files (by SHA-256)     |
| `sparsify <image>`             | -       | Remove zero-filled blocks from a container image            |
| `densify <image>`              | -       | Pre-allocate all blocks in a container image                |

Examples:

```bash
cwb list archive.zip
cwb extract archive.7z -o ./output
cwb x archive.rar -p mypassword
cwb create output.zip myDir file1.txt *.txt
cwb create output.7z file.txt --method lzma2+
cwb convert input.tar.gz output.tar.xz
cwb optimize input.zip optimized.zip
cwb benchmark largefile.bin
cwb analyze unknown.bin
cwb auto-extract sample.vhd --recursive
cwb suggest big.csv        # "→ consider zstd -19 (columnar/text, moderate entropy)"
cwb defragment disk.img --mode pack-start
cwb shrink disk.img
cwb wipe-empty disk.img
cwb convert-archive disk.d64 output.zip     # retro FS → modern archive
cwb convert-archive archive.zip out.tar     # archive → archive
cwb convert-archive archive.zip out.img -f fat # archive → filesystem image
cwb dedup disk.img --dry-run
cwb sparsify disk.vhd
cwb deploy disk.img \\.\PhysicalDrive2 --yes
```

**3-tier conversion model.** `cwb convert` picks the cheapest strategy that preserves data:

| Tier | Strategy                                     | Example                          |
| ---- | -------------------------------------------- | -------------------------------- |
| 1    | Bitstream transfer (zero decompression)      | `.gz` ↔ `.zlib`, `.zip` ↔ `.gz`  |
| 2    | Container restream (decompress wrapper only) | `.tar.gz` → `.tar.xz`            |
| 3    | Full recompress (extract + re-encode)        | `.zip` → `.7z`                   |

**Method+ system.** Append `+` to any method name for optimal encoding: `deflate+` uses Zopfli, `lzma+` uses Best, `lz4+` uses HC.

**Tool templates.** `cwb tool` registers external CLI tools (7z, binwalk, file, trid, …) in `~/.cwb-tools.json`. Templates use `{input}`, `{output}`, `{outputDir}` placeholders and can capture stdout, pipe stdin, or set a timeout. `cwb tool init` pre-populates templates for common tools.

### Compression.UI — WPF browser + analyser + heatmap

The archive browser is the conventional half: file list with icons, columns (name, size, compressed, ratio, method, modified), open / extract / create / test flows, preview window (text + hex), properties dialog with compression-ratio visualisation, benchmark tool, and Explorer context-menu integration (`Compression.Shell`).

**UI niceties** that match power-user expectations from 7-Zip / Total Commander:

- **".." everywhere** — navigates up one folder; at archive root it exits to **OS-browser mode** rooted at the archive's containing folder, so you can keep walking up the filesystem like 7z does.
- **Auto-descent into nested archives** — double-clicking a file inside an archive that's itself an archive (e.g. a `.vhd` inside a `.zip`) opens it as a new archive context. ".." pops back to the parent. Guarded by content-hash dedup + max-depth-16 cap so a malformed file detected as containing itself doesn't loop forever.
- **Drag in / drag out** — drop files on the window to open them or add them to the open archive (auto-detects); drag entries out of the list to copy them into Explorer or any drop target.
- **Last-folder restore** — relaunching the app reopens the OS browser at the last folder a file was opened from. If that folder was deleted in the meantime, walks up parents until one exists, falling back to `%USERPROFILE%`.
- **All file-type filters** — Open dialog dropdown lists "All Archives" + one entry per registered descriptor (auto-discovered, alphabetically sorted), so you can narrow to e.g. "ZIP archive (*.zip)" or "VMDK virtual disk (*.vmdk)" with one click.

The analyser is the interesting half. **When you drop an unknown binary on the UI, it never says "unsupported" — it shows you what the bytes look like.** The Binary Analysis wizard has a toolbar that walks you through progressively deeper investigation:

- **Scan Results** — every registered magic-byte signature that matches, with offsets and confidence.
- **Fingerprints** — algorithm identification from byte-distribution and byte-pair statistics.
- **Entropy Map** — per-region entropy profile with CUSUM change-point detection and 1D-Canny edge sharpening. Structured data (text, tables) shows low entropy; compressed/encrypted regions show high entropy; boundaries between them are marked.
- **Trial Decompress** — runs every registered stream decompressor in parallel with per-trial timeout and early-terminates on a low-entropy output. If any decoder produces plausible output, it is offered for preview.
- **Chain** — multi-layer compression reconstruction (e.g. `gzip(bzip2(data))`). Recursive trial decompression continues until entropy stops dropping.
- **Statistics** — full byte distribution, bigram histogram, chi-square randomness test, longest run, run-length distribution.
- **Strings** — ASCII / UTF-8 / UTF-16 string search with regex support.
- **Structure** — ImHex/010-style `.cwbt` templates. Built-in templates ship for ZIP, PNG, BMP, ELF, Gzip; you can write your own using u8-u64 / i8-i64 / f16-f64 (LE/BE), char/u8 arrays, BCD, fixed-point, color, date/time, and network types with dynamic length via field references or repeat-to-EOF.

### Heatmap Explorer

The Heatmap Explorer is the *visual* first pass. A 16×16 colour grid represents a proportional region of the file. Each of the 256 cells is one tile.

| Cell colour | Meaning                                          | Entropy  |
| ----------- | ------------------------------------------------ | -------- |
| Blue        | Low entropy — zeros, padding, simple headers     | 0.0–3.0  |
| Green       | Structured data — tables, records, text          | 3.0–5.5  |
| Orange      | Compressed data                                  | 5.5–7.5  |
| Red         | Random / encrypted (incompressible)              | 7.5–8.0  |
| Purple      | A known format signature was detected here       | any      |

Click any cell to subdivide into another 16×16 grid — it recursively zooms in on a region. Hovering shows offset, size, entropy, unique-byte count, and the detected signature (if any). **Extract** on a purple cell saves just that region to a file. The explorer only samples each block, so it handles arbitrarily large files without loading them into memory. Accessible from the analyser's "Heatmap" tab.

### Compression.Analysis — the analyser as a library

Everything the UI exposes is available as a .NET library under `Compression.Analysis`:

- **Signature Scanner** — magic-byte detection for every registered format (hash-indexed, O(n)).
- **Algorithm Fingerprinting** — statistical fingerprinting against known compression-output distributions.
- **Trial Decompression** — `TryAllAsync` runs every registered stream decompressor in parallel with per-trial timeout and early termination.
- **Chain Reconstruction** — discovers layered compression.
- **Entropy Mapping** — per-region entropy profiling with boundary detection; multi-resolution entropy pyramid (64 KB / 8 KB / 1 KB / 256 B), CUSUM binary segmentation, KL-divergence + chi-square boundary validation, 1D-Canny edge sharpening.
- **String Extraction** — ASCII / UTF-8 / UTF-16 with regex.
- **Structure Templates** — `.cwbt` template language.
- **Streaming Analysis** — reads the first 64 KB for magic/header; computes entropy in 64 KB chunks; returns per-chunk entropy profiles for arbitrarily large files.
- **Black-box tool integration** — `ExternalToolRunner`, `ToolOutputParser`, `CrossValidator`, `FallbackDecompressor` with auto-discovery of tools on `PATH`.
- **AutoExtractor** — recursive nested extraction: archives inside archives, disk images → partition tables → filesystems → files. Configurable max depth (default 5) and file-size limits.
- **BatchAnalyzer** — parallel directory scan with aggregate format statistics.
- **FileCarver / FileCarverOutputSink** — photorec-style flat magic-scan carving for damaged dumps. Streams 1 MB windows with 64 KB overlap; never materialises multi-GB images.
- **FilesystemCarver / FilesystemExtractor** — finds filesystem superblocks anywhere in a stream (ext at +1080, FAT at +54/+82, XFS "XFSB" at 0, Btrfs at 0x10020, …), validates each via the matching reader's `List()`, extracts contents per-file with isolated error handling.
- **RecursiveFilesystemCarver** — descends through wrapper chains: VHD → MBR → FAT → file.zip etc. Each `NestedHit` carries its `EnvelopeStack` lineage so consumers know what's wrapping what.
- **BlockMap / BlockMapRenderer** — colored visualization of envelope stacks. ASCII / SVG / HTML output with per-format palette (ext = green, FAT = orange, NTFS = blue, Btrfs = teal, XFS = red, MBR/GPT = grey, QCOW2/VMDK/VHD = purples). Used by `cwb visualize`.
- **PayloadCarver, StringsExtractor, EntropyHeatmap** — standalone helpers.

**Detection pipeline.** Magic bytes → parallel trial decompression (early-termination on low-entropy output) → extension fallback → deep probe (header parse + structural validation + integrity check).

**Partition table support.** `MbrParser` (four primaries at 0x1BE + extended/logical chain) + `GptParser` (EFI PART at LBA 1) + `PartitionTypeDatabase` (type-byte / GUID → filesystem name). Recursive descent via `--recursive`: disk image → partition table → filesystem → archive chain.

### Reverse Engineering

Two complementary flows for reverse-engineering unknown compression tools and file formats.

**Black-box tool probing** runs the target tool with a battery of controlled probe inputs (empty, single byte, incrementing patterns, text, random data, various sizes 0–64 KB), cross-correlates all outputs, and reports: magic bytes, size-field offsets (LE/BE, 2/4/8 byte), the compression algorithm (trial decompression against all building blocks), filename storage (UTF-8 / UTF-16), determinism, payload entropy.

```bash
cwb reverse-engineer MyTool.exe "{input} {output}"
cwb reverse-engineer packer.exe "--pack {input} --out {output}" --timeout 10000
```

The GUI offers the same via **Tools → Reverse Engineer Format** as a step-by-step wizard with progress reporting.

**Static analysis mode** works when you have archive files with known original content but no tool to run. `StaticFormatAnalyzer` accepts pairs of (original, archived) and locates where the content appears inside the archive — verbatim or compressed with any known building block — then infers header/footer structure, size fields, and compression algorithm without ever executing an external tool.

### Compression.Shell

Explorer context-menu integration. Right-click any file to invoke `cwb` commands directly: list, extract, test, optimise.

### Compression.Sfx.Cli / Compression.Sfx.Ui

Self-extracting archive stubs for console and GUI use. The stub is a normal `cwb`-style reader prepended to an archive overlay; running the resulting exe extracts in place. Used for single-file distributions via Costura.Fody.

---

## External tool validation

The test suite includes three tiers of external validation beyond the standard self-round-trip tests.

**Self round-trip.** All formats that support both create and extract are tested by creating an archive, extracting it, and verifying the output matches the original. Runs as part of the normal `dotnet test`.

**External tool interop** (`Category=EndToEnd`). Verifies our output is readable by external tools and vice versa. Dynamic tool discovery via `PATH` and common install locations; gracefully skips when tools are unavailable. Covered: **7z**, **gzip**, **bzip2**, **xz**, **zstd**, **lz4**, **tar**. Both directions are tested: create with our library → read with external tool, and vice versa.

```bash
dotnet test --filter "Category=EndToEnd"
```

**.NET BCL interop.** Verifies interoperability with `System.IO.Compression` (`GZipStream`, `DeflateStream`, `BrotliStream`, `ZipArchive`).

**OS integration** (`Category=OsIntegration`). Platform-specific tooling:

- **Windows** — PowerShell `Compress-Archive` / `Expand-Archive`, Windows `tar`, `certutil`, `Mount-DiskImage`, DISM
- **Linux** — `mtools` (FAT), `genisoimage` (ISO), `qemu-img` (virtual disks), `debugfs` (ext4), `cpio`

Platform detection + `Assert.Ignore` means tests never fail due to missing prerequisites.

```bash
dotnet test --filter "Category=OsIntegration"
```

**Filesystem validation matrix.** `Compression.Tests/ExternalFsInteropTests.cs` wires 18 filesystem-image tests against the tools below:

| Tool                | Present?                                        | Validates                                                                        |
| ------------------- | ----------------------------------------------- | -------------------------------------------------------------------------------- |
| 7-Zip (portable)    | Bundled                                         | NTFS, FAT, exFAT, ext, HFS, HFS+, ISO 9660, UDF, SquashFS, CramFS (list/extract) |
| qemu-img            | Optional — install from https://qemu.weilnetz.de/w64/ | VHD, VMDK, QCOW2, VDI (info + check)                                       |
| DISM                | Windows built-in                                | WIM, VHD, ISO                                                                    |
| chkdsk              | Windows built-in (admin + mounted volume)       | FAT, exFAT, NTFS                                                                 |
| mtools              | Optional — install from Cygwin                  | FAT (non-admin)                                                                  |
| WSL + mkfs.* / fsck.* | Optional — `wsl --install` as admin + reboot  | ext / XFS / Btrfs / F2FS / JFS / ReiserFS / UDF / UFS                            |
| DOSBox-X + MS-DOS 6.0/6.2 | Opt-in — set `CWB_MSDOS_DBLSPACE_BOOT_IMG`  | DBLSPACE CVF (`DBLSPACE /CHKDSK D:`) — see [`Compression.Tests/Support/MsDosImageStaging.md`](Compression.Tests/Support/MsDosImageStaging.md) |
| DOSBox-X + MS-DOS 6.22    | Opt-in — set `CWB_MSDOS_DRVSPACE_BOOT_IMG`  | DRVSPACE CVF (`DRVSPACE /CHKDSK D:`) — see [`Compression.Tests/Support/MsDosImageStaging.md`](Compression.Tests/Support/MsDosImageStaging.md) |
| DOSBox-X + FreeDOS LiveCD | Auto (hash-pinned download)                  | FAT (`CHKDSK D:` from FreeDOS) — gate is `[Explicit]` because the LiveCD welcome screen races the autoexec |

Tests skip cleanly when the tool is missing; they never fail the suite on a tool-deficient machine.

---

## Architecture

**Principles:**

1. **No external compression code.** Every algorithm is implemented from scratch in C#.
2. **Composable primitives.** `Compression.Core` provides the building blocks; `FileFormat.*` / `FileSystem.*` projects compose them. `Compression.Core` never implements format interfaces — it is pure algorithm.
3. **Stream-oriented.** All compression / decompression operates on `System.IO.Stream`.
4. **Immutable headers.** File-format header structures are immutable record types.
5. **Testability.** Every component is independently testable; NUnit tests cover primitives, format round-trips, and external interop.
6. **.NET 10 / C# 14.** Latest language features, nullable reference types, warnings-as-errors.

**Registry.** The source generator (`Compression.Registry.Generator`) emits a `RegisterFormats()` method listing every `IFormatDescriptor` and a `FormatDetector.Format` enum with one entry per format — zero reflection, zero hand-maintained lists. The same mechanism discovers `IBuildingBlock` implementations in `Compression.Core`.

---

## Building

```bash
dotnet build CompressionWorkbench.slnx
```

### Running the UI on Linux (Wine)

The UI project targets `net10.0-windows` (WPF) and cannot run natively on Linux.
Use the provided helper script to build and launch it under Wine:

```bash
./run-wine.sh            # first run: publishes a self-contained Windows exe, then launches
./run-wine.sh --rebuild  # force a fresh publish (e.g. after pulling new changes)
```

**Prerequisites:** Wine must be installed (`sudo pacman -S wine` on Arch/CachyOS).
For better font rendering install Core Fonts once: `winetricks corefonts`.

## Testing

```bash
dotnet test
```

---

## References to learn from

- **RFCs**: [RFC 1951](https://www.rfc-editor.org/rfc/rfc1951) (Deflate), [RFC 1952](https://www.rfc-editor.org/rfc/rfc1952) (Gzip), [RFC 1950](https://www.rfc-editor.org/rfc/rfc1950) (Zlib), [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932) (Brotli), [RFC 8878](https://www.rfc-editor.org/rfc/rfc8878) (Zstandard)
- **[libxad](https://github.com/ashang/libxad)** — the external archive decompressor, format reference
- **[XADMaster / The Unarchiver](https://github.com/MacPaw/XADMaster)** — modern continuation of libxad
- **[libarchive](https://github.com/libarchive/libarchive)** — multi-format reference
- **[Wikipedia list of archive formats](https://en.wikipedia.org/wiki/List_of_archive_formats)**
- **[ArchiveTeam Just Solve The File Format Problem](http://fileformats.archiveteam.org/wiki/Compression)** — compression format documentation
- **[7-Zip](https://github.com/ip7z/7zip)** — multi-archiver reference
- **[Matt Mahoney's data-compression page](https://mattmahoney.net/dc/)** — context-mixing compressors + corpus

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see [LICENSE](LICENSE).
