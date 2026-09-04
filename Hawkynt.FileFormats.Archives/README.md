# Hawkynt.FileFormats.Archives

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.FileFormats.Archives.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Archives/)
[![NuGet downloads](https://img.shields.io/nuget/dt/Hawkynt.FileFormats.Archives.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Archives/)
[![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
![Target](https://img.shields.io/badge/target-net10.0-blue)

> Pure-managed archive handling for .NET on top of `Hawkynt.Compression.Core`. The package claims the
> WHOLE domain — every compression stream, archive container, software package, document bundle,
> installer payload, game archive, backup image, executable packer and media container — not a
> selection of it. The support matrix below is the one ledger for that claim: every row is read from
> the format descriptor the package ships, and anything a row does not cover is a tracked gap.

## 📦 Installation

```bash
dotnet add package Hawkynt.FileFormats.Archives
```

The package bundles the archive-domain `FileFormat.*` assemblies and takes `Hawkynt.Compression.Core` as its one NuGet dependency. No native `zlib`, `liblzma`, `libarchive` or `libbz2` is loaded at runtime.

## ✨ Features

- Compression-stream readers and writers for modern and historical formats, including the encodings (BinHex, MacBinary, uuencode, yEnc).
- Archive enumeration, extraction, test, fresh creation and — for most containers — add/replace/remove on an existing archive.
- Maintenance verbs on the same surface: defragment, shrink, wipe unused space, optimize layout, reorder metadata.
- Software-package and installer inspection without executing the package or installer.
- Office, OpenDocument, e-book, mail and web bundles exposed through the same archive surface.
- Game, engine, console, Amiga and vintage archives beside the mainstream ZIP / TAR / 7z / RAR / CAB families.
- Backup and disk-image containers, executable images and resources, scientific data containers and media containers as pseudo-archives: one entry per addressable payload, track or stream.
- One `IArchiveFormatOperations` model for every container; `IStreamFormatOperations` for every single-stream codec.

## 🧩 Support matrix

| State | Meaning |
| --- | --- |
| **R** | List / extract / test only. |
| **WORM** | Read plus create a fresh archive; no edit of an existing one. |
| **R/W** | Read plus add / replace / remove on an existing archive. The edit may be byte-preserving in place or a verified extract → edit → re-create rebuild; both keep the result valid. |

Column legend: **Id** is the registry identifier (`FormatRegistry.GetById`, `cwb formats`). **Test** — the descriptor verifies checksums/structure (`CanTest`). **Maintenance** — the verbs the descriptor implements: `defrag` (`IArchiveDefragmentable`), `shrink` (`IArchiveShrinkable`), `wipe` (`IWipeEmpty` / `IArchiveLayoutMap`), `optimize` (`ILayoutOptimizable` or `SupportsOptimize`), `reorder` (`IFileInternalChunkMover`, moving container metadata such as MP4 `moov` or Matroska `Cues` in place). For media containers **Demux** is per-track extraction, **Mux** is building a container from elementary streams, **Remux / edit** is in-place relayout or editing. **Notes** name the deliberate subset or the naming quirk worth knowing; formats that do not preserve arbitrary entry names say so there.

Every State, Test, Maintenance, Compress/Decompress and Demux/Mux/Remux cell is derived from the descriptor's `Capabilities` and the interfaces its operations object implements; `Compression.Tests.Operations.ArchivesReadmeStateTests` fails when a cell disagrees with the built registry, so the table cannot drift from the code.

### 🧵 Compression streams and encodings

| Format | Id | Extensions | Compress | Decompress | Optimize | Notes | Reference |
| --- | --- | --- | :---: | :---: | :---: | --- | --- |
| aPLib | `ApLib` | `.aplib` | ✅ | ✅ | — | Self-framed stream; not byte-compatible with packer aPLib payloads (BB_Aplib decodes those) | [ibsensoftware.com](https://ibsensoftware.com/products_aPLib.html) |
| BALZ | `Balz` | `.balz` | ✅ | ✅ | — |  | [sourceforge.net](https://sourceforge.net/projects/balz/) |
| BCM | `Bcm` | `.bcm` | ✅ | ✅ | — |  | [GitHub](https://github.com/encode84/bcm) |
| [BinHex](https://en.wikipedia.org/wiki/BinHex) | `BinHex` | `.hqx` | ✅ | ✅ | — |  | [RFC](https://www.rfc-editor.org/rfc/rfc1741) |
| BriefLZ | `BriefLz` | `.blz` | ✅ | ✅ | — |  | [GitHub](https://github.com/jibsen/brieflz) |
| [Brotli](https://en.wikipedia.org/wiki/Brotli) | `Brotli` | `.br` | ✅ | ✅ | ✅ |  | [RFC](https://www.rfc-editor.org/rfc/rfc7932) |
| BSC | `Bsc` | `.bsc` | ✅ | ✅ | — |  | [GitHub](https://github.com/IlyaGrebnov/libbsc) |
| [bzip2](https://en.wikipedia.org/wiki/Bzip2) | `Bzip2` | `.bz2` `.bzip2` | ✅ | ✅ | ✅ |  | [sourceware.org](https://sourceware.org/bzip2/manual/manual.html) |
| cmix | `Cmix` | `.cmix` | ✅ | ✅ | — |  | [GitHub](https://github.com/byronknoll/cmix) |
| [Unix compress (.Z)](https://en.wikipedia.org/wiki/Compress_(software)) | `Compress` | `.z` | ✅ | ✅ | ✅ |  | [pubs.opengroup.org](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/compress.html) |
| CP/M Crunch | `Crunch` | `.cru` | ✅ | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Crunch) |
| CSC | `Csc` | `.csc` | ✅ | ✅ | — |  | [GitHub](https://github.com/fusiyuan2010/CSC) |
| Density | `Density` | `.density` | ✅ | ✅ | — |  | [GitHub](https://github.com/k0dai/density) |
| Freeze | `Freeze` | `.f` `.freeze` | ✅ | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Freeze) |
| [gzip](https://en.wikipedia.org/wiki/Gzip) | `Gzip` | `.gz` `.gzip` | ✅ | ✅ | ✅ |  | [RFC](https://www.rfc-editor.org/rfc/rfc1952) |
| ICE Packer | `IcePacker` | `.ice` | ✅ | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/ICE) |
| KWAJ | `Kwaj` |  | ✅ | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/KWAJ) |
| Lizard (LZ5) | `Lizard` | `.liz` | ✅ | ✅ | — |  | [GitHub](https://github.com/inikep/lizard) |
| [LZ4 frame](https://en.wikipedia.org/wiki/LZ4_(compression_algorithm)) | `Lz4` | `.lz4` | ✅ | ✅ | ✅ |  | [GitHub](https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md) |
| [LZFSE](https://en.wikipedia.org/wiki/LZFSE) | `Lzfse` | `.lzfse` | ✅ | ✅ | — | Uncompressed and LZVN blocks only; the FSE/tANS compressed block families are not implemented | [GitHub](https://github.com/lzfse/lzfse) |
| LZG | `Lzg` | `.lzg` | ✅ | ✅ | — |  | [GitHub](https://github.com/mbitsnbites/liblzg) |
| LZHAM | `Lzham` | `.lzham` | ✅ | ✅ | — |  | [GitHub](https://github.com/richgel999/lzham_codec) |
| [Lzip](https://en.wikipedia.org/wiki/Lzip) | `Lzip` | `.lz` `.lzip` | ✅ | ✅ | ✅ |  | [nongnu.org](https://www.nongnu.org/lzip/manual/lzip_manual.html#File-format) |
| [LZMA (.lzma)](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Markov_chain_algorithm) | `Lzma` | `.lzma` | ✅ | ✅ | ✅ |  | [7-zip.org](https://www.7-zip.org/sdk.html) |
| [lzop](https://en.wikipedia.org/wiki/Lzop) | `Lzop` | `.lzo` | ✅ | ✅ | ✅ |  | [lzop.org](https://www.lzop.org/) |
| [LZS](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Stac) | `Lzs` | `.lzs` | ✅ | ✅ | — |  | [RFC](https://www.rfc-editor.org/rfc/rfc2395) |
| [MacBinary](https://en.wikipedia.org/wiki/MacBinary) | `MacBinary` | `.bin` `.macbin` | ✅ | ✅ | — |  | [RFC](https://www.rfc-editor.org/rfc/rfc1740) |
| MCM | `Mcm` | `.mcm` | ✅ | ✅ | — |  | [GitHub](https://github.com/mathieuchartier/mcm) |
| [PackBits](https://en.wikipedia.org/wiki/PackBits) | `PackBits` | `.packbits` | ✅ | ✅ | — |  | [developer.apple.com](https://developer.apple.com/library/archive/documentation/mac/pdf/MoreMacintoshToolbox.pdf) |
| [PAQ8](https://en.wikipedia.org/wiki/PAQ) | `Paq8` | `.paq8l` `.paq8` | ✅ | ✅ | — |  | [mattmahoney.net](https://mattmahoney.net/dc/paq.html) |
| PowerPacker | `PowerPacker` | `.pp` `.pp20` | ✅ | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/PowerPacker) |
| [PPMd](https://en.wikipedia.org/wiki/Prediction_by_partial_matching) | `Ppmd` | `.pmd` | ✅ | ✅ | — |  | [7-zip.org](https://www.7-zip.org/sdk.html) |
| QuickLZ | `QuickLz` | `.quicklz` | ✅ | ✅ | — |  | [quicklz.com](http://www.quicklz.com/) |
| RefPack / QFS | `RefPack` | `.qfs` `.refpack` | ✅ | ✅ | — |  | [wiki.niotso.org](http://wiki.niotso.org/RefPack) |
| RNC ProPack | `Rnc` | `.rnc` | ✅ | ✅ | — |  | [segaretro.org](https://segaretro.org/Rob_Northen_compression) |
| [rzip](https://en.wikipedia.org/wiki/Rzip) | `Rzip` | `.rz` `.rzip` | ✅ | ✅ | — |  | [rzip.samba.org](https://rzip.samba.org/) |
| [Snappy](https://en.wikipedia.org/wiki/Snappy_(compression)) | `Snappy` | `.sz` `.snappy` | ✅ | ✅ | — |  | [GitHub](https://github.com/google/snappy/blob/main/framing_format.txt) |
| Squeeze (SQ) | `Squeeze` | `.sqz` | ✅ | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/SQ) |
| [SWF](https://en.wikipedia.org/wiki/SWF) | `Swf` | `.swf` | ✅ | ✅ | — | FWS/CWS envelope; compress and decompress the body, no tag-level parsing | [open-flash.github.io](https://open-flash.github.io/mirrors/swf-spec-19.pdf) |
| SZ (MS COMPRESS, KWAJ-less) | `SzCompress` |  | ✅ | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/SZDD) |
| SZDD | `Szdd` |  | ✅ | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/SZDD) |
| [uuencode](https://en.wikipedia.org/wiki/Uuencoding) | `UuEncoding` | `.uue` `.uu` | ✅ | ✅ | — |  | [pubs.opengroup.org](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/uuencode.html) |
| [XZ](https://en.wikipedia.org/wiki/XZ_Utils) | `Xz` | `.xz` | ✅ | ✅ | ✅ |  | [tukaani.org](https://tukaani.org/xz/xz-file-format.txt) |
| [yEnc](https://en.wikipedia.org/wiki/YEnc) | `YEnc` | `.yenc` | ✅ | ✅ | — |  | [yenc.org](http://www.yenc.org/yenc-draft.1.3.txt) |
| Yaz0 | `Yaz0` | `.yaz0` `.szs` | ✅ | ✅ | — |  | [wiki.tockdom.com](https://wiki.tockdom.com/wiki/YAZ0) |
| [zlib](https://en.wikipedia.org/wiki/Zlib) | `Zlib` | `.zlib` | ✅ | ✅ | ✅ |  | [RFC](https://www.rfc-editor.org/rfc/rfc1950) |
| Zling | `Zling` | `.zling` | ✅ | ✅ | — |  | [GitHub](https://github.com/richox/libzling) |
| [Zstandard](https://en.wikipedia.org/wiki/Zstd) | `Zstd` | `.zst` `.zstd` | ✅ | ✅ | ✅ |  | [RFC](https://www.rfc-editor.org/rfc/rfc8878) |

### 🗜️ Archive containers

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| [ACE](https://en.wikipedia.org/wiki/ACE_(compressed_file_format)) | `Ace` | `.ace` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/droe/acefile) |
| [afio](https://en.wikipedia.org/wiki/Afio) | `Afio` | `.afio` | WORM | ✅ | — | Writes stored members only; the per-file gzip extension is read but not written | [GitHub](https://github.com/kholtman/afio) |
| [ALZip](https://en.wikipedia.org/wiki/ALZip) | `AlZip` | `.alz` | R/W | ✅ | defrag · wipe |  | [kippler.com](http://kippler.com/win/unalz/) |
| AMPK (Amiga Pack) | `Ampk` | `.ampk` | R/W | ✅ | defrag · wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/AmiPack) |
| [AR](https://en.wikipedia.org/wiki/Ar_(Unix)) | `Ar` | `.a` `.ar` `.deb` | R/W | ✅ | defrag · wipe |  | [freebsd.org](https://www.freebsd.org/cgi/man.cgi?query=ar&sektion=5) |
| [ARC](https://en.wikipedia.org/wiki/ARC_(file_format)) | `Arc` | `.arc` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/hyc/arc) |
| [ARJ](https://en.wikipedia.org/wiki/ARJ) | `Arj` | `.arj` | R/W | ✅ | defrag · wipe |  | [arj.sourceforge.net](https://arj.sourceforge.net) |
| [Binary II](https://en.wikipedia.org/wiki/Binary_II) | `BinaryII` | `.bny` `.bqy` | R/W | ✅ | defrag · wipe |  | [mirrors.apple2.org.za](https://mirrors.apple2.org.za/ground.icaen.uiowa.edu/MiscInfo/Binary2/bin2.specs) |
| [CAB](https://en.wikipedia.org/wiki/Cabinet_(file_format)) | `Cab` | `.cab` | R/W | ✅ | defrag · wipe |  | [cabextract.org.uk](https://www.cabextract.org.uk/libmspack/) |
| [CB7](https://en.wikipedia.org/wiki/Comic_book_archive) | `Cb7` | `.cb7` | R/W | ✅ | defrag · wipe | 7z-backed comic book archive | [7-zip.org](https://www.7-zip.org/7z.html) |
| [CBR](https://en.wikipedia.org/wiki/Comic_book_archive) | `Cbr` | `.cbr` | R/W | ✅ | defrag · wipe | RAR-backed comic book archive | [rarlab.com](https://www.rarlab.com/technote.htm) |
| [CBZ](https://en.wikipedia.org/wiki/Comic_book_archive) | `Cbz` | `.cbz` | R/W | ✅ | defrag · wipe | ZIP-backed comic book archive | [pkware.cachefly.net](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT) |
| [CHM](https://en.wikipedia.org/wiki/Microsoft_Compiled_HTML_Help) | `Chm` | `.chm` | R/W | ✅ | defrag · wipe |  | [cabextract.org.uk](https://www.cabextract.org.uk/libmspack/) |
| [Compact Pro](https://en.wikipedia.org/wiki/Compact_Pro) | `CompactPro` | `.cpt` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/MacPaw/XADMaster) |
| [CPIO](https://en.wikipedia.org/wiki/Cpio) | `Cpio` | `.cpio` | R/W | ✅ | defrag · wipe |  | [pubs.opengroup.org](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html) |
| [DAR (Disk ARchive)](https://en.wikipedia.org/wiki/Dar_(disk_archiver)) | `Dar` | `.dar` | R | ✅ | — |  | [dar.linux.free.fr](http://dar.linux.free.fr) |
| DCS (Amiga) | `Dcs` | `.dcs` | WORM | ✅ | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| [DiskDoubler](https://en.wikipedia.org/wiki/DiskDoubler) | `DiskDoubler` | `.dd` `.sea` | WORM | — | defrag · wipe | Single-fork compressor: one payload per file | [GitHub](https://github.com/MacPaw/XADMaster) |
| [DMS](https://en.wikipedia.org/wiki/Disk_Masher_System) | `Dms` | `.dms` | WORM | ✅ | defrag |  | [GitHub](https://github.com/markrabjohn/xDMS) |
| [EGG (ALZip)](https://en.wikipedia.org/wiki/EGG_(file_format)) | `Egg` | `.egg` | WORM | ✅ | defrag |  | [GitHub](https://github.com/alkegi/docs/blob/master/egg.md) |
| [ESD](https://en.wikipedia.org/wiki/Windows_Imaging_Format) | `Esd` | `.esd` | WORM | ✅ | wipe | Solid LZMS WIM; created images carry a metadata resource but entries re-list as resources | [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wim-and-esd-windows-image-files-overview) |
| [FreeArc](https://en.wikipedia.org/wiki/FreeArc) | `FreeArc` | `.arc` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/Bulat-Ziganshin/FA) |
| HA | `Ha` | `.ha` | R/W | ✅ | defrag · wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/HA) |
| [IFF CDAF](https://en.wikipedia.org/wiki/Interchange_File_Format) | `IffCdaf` | `.cdaf` | R/W | ✅ | defrag · wipe |  | [Aminet](https://aminet.net) |
| [LBR](https://en.wikipedia.org/wiki/LBR_(file_format)) | `Lbr` | `.lbr` | R/W | ✅ | defrag · wipe |  | [gaby.de](http://www.gaby.de/cpm/manuals/archive/lbr.txt) |
| LhF (LhFloppy) | `LhF` | `.lhf` | R/W | ✅ | defrag · wipe | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| [lrzip](https://en.wikipedia.org/wiki/Rzip#lrzip) | `Lrzip` | `.lrz` | WORM | ✅ | defrag | LZMA-wrapped subtype only; other lrzip subtypes are rejected; single data member | [GitHub](https://github.com/ckolivas/lrzip) |
| Lynx (Commodore) | `Lynx` | `.lnx` | R/W | ✅ | defrag · wipe | Stored entries only | [Archive Team](http://fileformats.archiveteam.org/wiki/Lynx_(Commodore_64)) |
| [LHA / LZH](https://en.wikipedia.org/wiki/LHA_(file_format)) | `Lzh` | `.lzh` `.lha` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/jca02266/lha) |
| [LZX (Amiga)](https://en.wikipedia.org/wiki/LZX) | `LzxAmiga` | `.lzx` | R/W | ✅ | defrag · wipe |  | [Aminet](https://aminet.net) |
| [NuFX / ShrinkIt](https://en.wikipedia.org/wiki/ShrinkIt) | `NuFx` | `.shk` `.sdk` `.bxy` | R/W | ✅ | defrag · shrink · wipe |  | [nulib.com](https://nulib.com/library/FTN.e08002.htm) |
| PackDisk (Amiga) | `PackDisk` | `.pdsk` | WORM | ✅ | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| PackIt | `PackIt` | `.pit` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/MacPaw/XADMaster) |
| [RAR](https://en.wikipedia.org/wiki/RAR_(file_format)) | `Rar` | `.rar` | R/W | ✅ | defrag · wipe | v1–v5 readers; creation and edits emit RAR4/RAR5 without claiming WinRAR encoder parity | [rarlab.com](https://www.rarlab.com/technote.htm) |
| [7z](https://en.wikipedia.org/wiki/7z) | `SevenZip` | `.7z` | R/W | ✅ | defrag · wipe |  | [7-zip.org](https://www.7-zip.org/7z.html) |
| [SHAR](https://en.wikipedia.org/wiki/Shar) | `Shar` | `.shar` `.sh` | R/W | ✅ | defrag | Add appends in place; Remove re-emits the script from the survivors | [gnu.org](https://www.gnu.org/software/sharutils/) |
| [Spark (RISC OS)](https://en.wikipedia.org/wiki/ARC_(file_format)) | `Spark` | `.spk` `.spark` | R/W | ✅ | defrag · wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Spark) |
| [Split File (.001)](https://en.wikipedia.org/wiki/File_spanning) | `SplitFile` | `.001` | WORM | ✅ | defrag |  | [Wikipedia](https://en.wikipedia.org/wiki/File_spanning) |
| [SQX](https://en.wikipedia.org/wiki/SQX) | `Sqx` | `.sqx` | R/W | ✅ | defrag · wipe |  | [encode.su](https://encode.su/threads/1290-SQX-(by-SpeedProject)) |
| [StuffIt](https://en.wikipedia.org/wiki/StuffIt) | `StuffIt` | `.sit` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/MacPaw/XADMaster) |
| [StuffIt X](https://en.wikipedia.org/wiki/StuffIt) | `StuffItX` | `.sitx` | WORM | ✅ | defrag · wipe | Writer emits the envelope shell only; the proprietary element catalog is not synthesised | [GitHub](https://github.com/MacPaw/XADMaster) |
| [Split WIM (.swm)](https://en.wikipedia.org/wiki/Windows_Imaging_Format) | `Swm` | `.swm` `.swm2` `.swm3` `.swm4` … | R | ✅ | wipe |  | [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wim-and-esd-windows-image-files-overview) |
| [T64 (Commodore tape image)](https://en.wikipedia.org/wiki/T64_(file_format)) | `T64` | `.t64` | R/W | ✅ | defrag · wipe |  | [vice-emu.sourceforge.io](https://vice-emu.sourceforge.io/) |
| [TAR](https://en.wikipedia.org/wiki/Tar_(computing)) | `Tar` | `.tar` | R/W | ✅ | defrag · shrink · wipe |  | [pubs.opengroup.org](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html) |
| UHARC | `Uharc` | `.uha` | R/W | ✅ | defrag · wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/UHARC) |
| [WIM](https://en.wikipedia.org/wiki/Windows_Imaging_Format) | `Wim` | `.wim` `.swm` `.esd` | WORM | ✅ | defrag · wipe | LZX / XPRESS / LZMS paths; kept create-only because an append edit would break the checksum chain | [wimlib.net](https://wimlib.net/) |
| Wrapster | `Wrapster` |  | WORM | ✅ | defrag · wipe | MP3-wrapper archive carrying one member; stays WORM by design | [Archive Team](http://fileformats.archiveteam.org/wiki/Wrapster) |
| [XAR](https://en.wikipedia.org/wiki/Xar_(archiver)) | `Xar` | `.xar` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/mackyle/xar) |
| xDisk / GDC (Amiga) | `xDisk` | `.xdsk` `.gdc` | WORM | ✅ | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| xMash (Amiga) | `xMash` | `.xmsh` | WORM | ✅ | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| ZAP (Amiga) | `Zap` | `.zap` | WORM | ✅ | defrag · wipe | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net/) |
| [ZIP](https://en.wikipedia.org/wiki/ZIP_(file_format)) | `Zip` | `.zip` `.zipx` | R/W | ✅ | defrag · shrink · wipe · optimize | Store, Deflate, Deflate64, Shrink, Reduce, Implode, BZip2, LZMA, PPMd, Zstd, AES | [pkware.cachefly.net](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT) |
| [ZOO](https://en.wikipedia.org/wiki/Zoo_(file_format)) | `Zoo` | `.zoo` | R/W | ✅ | defrag · wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/ZOO) |
| [ZPAQ](https://en.wikipedia.org/wiki/ZPAQ) | `Zpaq` | `.zpaq` | R/W | ✅ | defrag | Reader covers the stored/simple models; ZPAQL virtual-machine execution is not implemented | [mattmahoney.net](http://mattmahoney.net/dc/zpaq.html) |

### 📦 Software packages and installers

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| [Android App Bundle / split APK](https://en.wikipedia.org/wiki/Android_App_Bundle) | `AndroidBundle` | `.aab` `.apks` | R/W | ✅ | defrag · wipe |  | [developer.android.com](https://developer.android.com/guide/app-bundle) |
| Android OTA payload | `AndroidOta` |  | WORM | ✅ | — | Create emits a whole-image payload; it re-lists as payload blobs, not files | [source.android.com](https://source.android.com/docs/core/ota) |
| [APK](https://en.wikipedia.org/wiki/Apk_(file_format)) | `Apk` | `.apk` | R/W | ✅ | defrag · wipe |  | [developer.android.com](https://developer.android.com/guide/components/fundamentals) |
| [APK native libraries](https://en.wikipedia.org/wiki/Apk_(file_format)) | `ApkNativeLibs` |  | WORM | ✅ | wipe | Pseudo-archive over lib/<abi>/*.so | [developer.android.com](https://developer.android.com/ndk/guides/abis) |
| [AppImage](https://en.wikipedia.org/wiki/AppImage) | `AppImage` | `.AppImage` `.appimage` | WORM | ✅ | — | ELF stub plus appended SquashFS; creation delegates to the SquashFS writer | [GitHub](https://github.com/AppImage/AppImageSpec) |
| [APPX](https://en.wikipedia.org/wiki/Appx) | `Appx` | `.appx` `.msix` | R/W | ✅ | defrag · wipe |  | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/msix/) |
| [Android resources.arsc](https://en.wikipedia.org/wiki/Apk_(file_format)) | `Arsc` | `.arsc` | R | ✅ | — |  | [android.googlesource.com](https://android.googlesource.com/platform/frameworks/base/+/master/libs/androidfw/include/androidfw/ResourceTypes.h) |
| Electron asar | `Asar` | `.asar` | WORM | ✅ | — | JSON header plus concatenated payload | [GitHub](https://github.com/electron/asar) |
| BitRock InstallBuilder | `BitRock` |  | R | ✅ | — | Metakit VFS with LZMA payloads | [installbuilder.com](https://installbuilder.com) |
| [Rust crate](https://en.wikipedia.org/wiki/Cargo_(software)) | `Crate` | `.crate` | WORM | ✅ | — | tar.gz with the crate directory layout | [doc.rust-lang.org](https://doc.rust-lang.org/cargo/reference/registries.html#publish) |
| [CRX](https://en.wikipedia.org/wiki/Google_Chrome#Extensions) | `Crx` | `.crx` | WORM | ✅ | defrag · wipe | CRX3 envelope creation is unsigned and not browser-trusted | [chromium.googlesource.com](https://chromium.googlesource.com/chromium/src/+/main/components/crx_file/) |
| [Debian .deb](https://en.wikipedia.org/wiki/Deb_(file_format)) | `Deb` | `.deb` | R/W | ✅ | defrag · wipe |  | [debian.org](https://www.debian.org/doc/debian-policy/) |
| [EAR](https://en.wikipedia.org/wiki/EAR_(file_format)) | `Ear` | `.ear` | R/W | ✅ | defrag · wipe |  | [jakarta.ee](https://jakarta.ee/specifications/platform/) |
| [Ruby gem](https://en.wikipedia.org/wiki/RubyGems) | `Gem` | `.gem` | WORM | ✅ | — | TAR with gzip-compressed metadata and data members | [docs.ruby-lang.org](https://docs.ruby-lang.org/en/3.0/Gem/Format.html) |
| [Inno Setup](https://en.wikipedia.org/wiki/Inno_Setup) | `InnoSetup` |  | WORM | ✅ | defrag | Extraction plus signature/container output, not an installer compiler; some versions expose no per-file extraction | [sourceforge.net](https://sourceforge.net/projects/innounp/) |
| [IPA](https://en.wikipedia.org/wiki/.ipa) | `Ipa` | `.ipa` | R/W | ✅ | defrag · wipe |  | [pkware.cachefly.net](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT) |
| [JAR](https://en.wikipedia.org/wiki/JAR_(file_format)) | `Jar` | `.jar` | R/W | ✅ | defrag · wipe |  | [docs.oracle.com](https://docs.oracle.com/javase/8/docs/technotes/guides/jar/jar.html) |
| [MSI](https://en.wikipedia.org/wiki/Windows_Installer) | `Msi` | `.msi` `.msp` `.mst` | R/W | ✅ | defrag · wipe | CFB envelope; a functional Installer database is not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/) |
| [MSIX](https://en.wikipedia.org/wiki/MSIX) | `Msix` | `.msix` `.msixbundle` | R/W | ✅ | defrag · wipe | Unsigned fresh package output | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/msix/) |
| [NSIS](https://en.wikipedia.org/wiki/Nullsoft_Scriptable_Install_System) | `Nsis` |  | WORM | ✅ | defrag | Extraction plus overlay-oriented output, not a PE installer builder | [nsis.sourceforge.io](https://nsis.sourceforge.io/Docs/) |
| [NuGet .nupkg](https://en.wikipedia.org/wiki/NuGet) | `NuPkg` | `.nupkg` | R/W | ✅ | defrag · wipe |  | [Microsoft Learn](https://learn.microsoft.com/nuget/reference/nuspec) |
| [OVA](https://en.wikipedia.org/wiki/Open_Virtualization_Format) | `Ova` | `.ova` | WORM | ✅ | — | Stays WORM: the manifest must cover every member | [dmtf.org](https://www.dmtf.org/standards/ovf) |
| [Pack200](https://en.wikipedia.org/wiki/Pack200) | `Pack200` | `.pack` | R | ✅ | — |  | [docs.oracle.com](https://docs.oracle.com/javase/8/docs/technotes/guides/pack200/pack-spec.html) |
| [PyInstaller onefile](https://en.wikipedia.org/wiki/PyInstaller) | `PyInstaller` |  | R | ✅ | — | CArchive TOC plus PYZ modules; Linux builds are detected as ELF first | [GitHub](https://github.com/pyinstaller/pyinstaller) |
| [RPM](https://en.wikipedia.org/wiki/RPM_Package_Manager) | `Rpm` | `.rpm` | WORM | ✅ | defrag · wipe |  | [GitHub](https://github.com/rpm-software-management/rpm) |
| [Snap](https://en.wikipedia.org/wiki/Snap_(software)) | `Snap` | `.snap` | WORM | ✅ | — | SquashFS package | [snapcraft.io](https://snapcraft.io/docs) |
| [WAR](https://en.wikipedia.org/wiki/WAR_(file_format)) | `War` | `.war` | R/W | ✅ | defrag · wipe |  | [jakarta.ee](https://jakarta.ee/specifications/servlet/) |
| [Python wheel](https://en.wikipedia.org/wiki/Wheel_(software)) | `Wheel` | `.whl` | WORM | ✅ | wipe | ZIP plus dist-info | [peps.python.org](https://peps.python.org/pep-0427/) |
| [XPI](https://en.wikipedia.org/wiki/XPInstall) | `Xpi` | `.xpi` | R/W | ✅ | defrag · wipe |  | [extensionworkshop.com](https://extensionworkshop.com/) |

### 📄 Documents, e-books, mail and web bundles

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| [Adobe Illustrator](https://en.wikipedia.org/wiki/Adobe_Illustrator_Artwork) | `Ai` |  | R | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Adobe_Illustrator) |
| [DOC](https://en.wikipedia.org/wiki/Doc_(computing)) | `Doc` | `.doc` | R/W | ✅ | defrag · wipe | CFB envelope; Word document streams are not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-doc/) |
| [DOCX](https://en.wikipedia.org/wiki/Office_Open_XML) | `Docx` | `.docx` | R/W | ✅ | defrag · wipe |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-376/) |
| [EML](https://en.wikipedia.org/wiki/Email#Message_format) | `Eml` | `.eml` | R/W | ✅ | — |  | [RFC](https://www.rfc-editor.org/rfc/rfc5322) |
| [EPUB](https://en.wikipedia.org/wiki/EPUB) | `Epub` | `.epub` | R/W | ✅ | defrag · wipe |  | [w3.org](https://www.w3.org/TR/epub-33/) |
| [FB2](https://en.wikipedia.org/wiki/FictionBook) | `Fb2` | `.fb2` | R | ✅ | — |  | [GitHub](https://github.com/gribuser/fb2) |
| [FLA](https://en.wikipedia.org/wiki/Adobe_Animate) | `Fla` |  | R | ✅ | wipe | ZIP-based XFL document | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/) |
| [KMZ](https://en.wikipedia.org/wiki/Keyhole_Markup_Language) | `Kmz` | `.kmz` | R/W | ✅ | defrag · wipe |  | [developers.google.com](https://developers.google.com/kml/documentation) |
| [LIT (Microsoft Reader)](https://en.wikipedia.org/wiki/Microsoft_Reader) | `Lit` | `.lit` | R | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Microsoft_Reader) |
| [MAFF](https://en.wikipedia.org/wiki/Mozilla_Archive_Format) | `Maff` | `.maff` | R/W | ✅ | defrag · wipe |  | [maf.mozdev.org](http://maf.mozdev.org/maff-specification.html) |
| [mbox (Unix mailbox)](https://en.wikipedia.org/wiki/Mbox) | `Mbox` | `.mbox` `.mbx` | R/W | ✅ | — | Entries are message_NN.eml | [RFC](https://www.rfc-editor.org/rfc/rfc4155) |
| [MOBI / AZW](https://en.wikipedia.org/wiki/Mobipocket) | `Mobi` | `.mobi` `.prc` `.azw` `.azw3` | R | ✅ | — |  | [wiki.mobileread.com](https://wiki.mobileread.com/wiki/MOBI) |
| [MSG](https://en.wikipedia.org/wiki/MSG_(file_format)) | `Msg` | `.msg` | R/W | ✅ | defrag · wipe | CFB envelope; MAPI properties are not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxmsg/) |
| [ODP](https://en.wikipedia.org/wiki/OpenDocument) | `Odp` | `.odp` | R/W | ✅ | defrag · wipe |  | [libreoffice.org](https://www.libreoffice.org) |
| [ODS](https://en.wikipedia.org/wiki/OpenDocument) | `Ods` | `.ods` | R/W | ✅ | defrag · wipe |  | [libreoffice.org](https://www.libreoffice.org) |
| [ODT](https://en.wikipedia.org/wiki/OpenDocument) | `Odt` | `.odt` | R/W | ✅ | defrag · wipe |  | [libreoffice.org](https://www.libreoffice.org) |
| [Microsoft OneNote](https://en.wikipedia.org/wiki/Microsoft_OneNote) | `OneNote` | `.one` `.onetoc2` | R | ✅ | — |  | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-onestore/) |
| [PDF](https://en.wikipedia.org/wiki/PDF) | `Pdf` | `.pdf` | R/W | ✅ | defrag · wipe | Image extraction and file-attachment surface, not a page renderer or editor | [ISO](https://www.iso.org/standard/75839.html) |
| [PPT](https://en.wikipedia.org/wiki/Microsoft_PowerPoint) | `Ppt` | `.ppt` | R/W | ✅ | defrag · wipe | CFB envelope; presentation streams are not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-ppt/6be79dde-33c1-4c1b-8ccc-4b2301c08662) |
| [PPTX](https://en.wikipedia.org/wiki/Office_Open_XML) | `Pptx` | `.pptx` | R/W | ✅ | defrag · wipe |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-376/) |
| [PST / OST](https://en.wikipedia.org/wiki/Personal_Storage_Table) | `Pst` | `.pst` `.ost` | R | ✅ | — |  | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-pst/141923d5-15ab-4ef1-a524-6dce75aae546) |
| [Sketch](https://en.wikipedia.org/wiki/Sketch_(software)) | `Sketch` |  | R | ✅ | wipe |  | [developer.sketch.com](https://developer.sketch.com/file-format/) |
| [Thumbs.db](https://en.wikipedia.org/wiki/Windows_thumbnail_cache) | `ThumbsDb` | `.db` | R/W | ✅ | defrag · wipe | CFB envelope; catalog streams are not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/53989ce4-7b05-4f8d-829b-d08d6148375b) |
| [TNEF (winmail.dat)](https://en.wikipedia.org/wiki/Transport_Neutral_Encapsulation_Format) | `Tnef` | `.dat` `.tnef` | R/W | ✅ | defrag |  | [GitHub](https://github.com/Yeraze/ytnef) |
| [VSDX](https://en.wikipedia.org/wiki/Microsoft_Visio) | `Vsdx` | `.vsdx` `.vstx` `.vssx` `.vsdm` … | R/W | ✅ | defrag · wipe |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-376/) |
| WACZ | `Wacz` | `.wacz` | WORM | ✅ | wipe | ZIP around WARC plus package metadata | [specs.webrecorder.net](https://specs.webrecorder.net/wacz/1.1.1/) |
| [WARC](https://en.wikipedia.org/wiki/WARC_(file_format)) | `Warc` | `.warc` | WORM | ✅ | defrag · wipe | Entries are listed as "resource: name"; create emits resource records | [iipc.github.io](https://iipc.github.io/warc-specifications/) |
| Web Bundle | `Wbn` | `.wbn` | WORM | ✅ | — | Minimal CBOR walk; create collapses inputs into one bundle | [datatracker.ietf.org](https://datatracker.ietf.org/doc/draft-ietf-wpack-bundled-responses/) |
| [WordPerfect](https://en.wikipedia.org/wiki/WordPerfect) | `WordPerfect` | `.wpd` `.wp` `.wp5` `.wp6` … | R | ✅ | — |  | [sourceforge.net](https://sourceforge.net/projects/libwpd/) |
| [XLS](https://en.wikipedia.org/wiki/Microsoft_Excel#File_formats) | `Xls` | `.xls` | R/W | ✅ | defrag · wipe | CFB envelope; workbook streams are not synthesised | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-xls/cd03cb5f-ca02-4934-a391-bb674cb8aa06) |
| [XLSX](https://en.wikipedia.org/wiki/Office_Open_XML) | `Xlsx` | `.xlsx` | R/W | ✅ | defrag · wipe |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-376/) |
| [XPS / OpenXPS](https://en.wikipedia.org/wiki/Open_XML_Paper_Specification) | `Xps` | `.xps` `.oxps` | R/W | ✅ | defrag · wipe |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-388/) |

### 🎮 Game, engine and console archives

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| Sega AFS | `Afs` | `.afs` | R/W | ✅ | defrag · wipe | Alignment and metadata block paths | [GitHub](https://github.com/MaikelChan/AFSPacker) |
| Square Enix AKB | `Akb` | `.akb` | WORM | ✅ | defrag · wipe | Entries are entry_NNN.bin | [GitHub](https://github.com/vgmstream/vgmstream) |
| CRI AWB / AFS2 | `Awb` | `.awb` `.acb` | WORM | ✅ | defrag · wipe | Entries are cue_NNNNN.bin | [GitHub](https://github.com/vgmstream/vgmstream) |
| Bethesda BA2 | `Ba2` | `.ba2` | R/W | ✅ | defrag · wipe | BTDX GNRL scope | [en.uesp.net](https://en.uesp.net/wiki/Skyrim_Mod:File_Formats/BA2) |
| EA / Westwood BIG | `Big` | `.big` | R/W | ✅ | defrag · wipe |  | [MultimediaWiki](https://wiki.multimedia.cx/index.php/Electronic_Arts_Formats) |
| Bethesda BSA | `Bsa` | `.bsa` | R/W | ✅ | defrag · wipe |  | [en.uesp.net](https://en.uesp.net/wiki/Skyrim_Mod:File_Formats/BSA) |
| [Bloodlines DZIP](https://en.wikipedia.org/wiki/Vampire:_The_Masquerade_%E2%80%93_Bloodlines) | `Dzip` | `.dzip` | R/W | ✅ | defrag · wipe |  | — |
| [GameMaker data.win](https://en.wikipedia.org/wiki/GameMaker) | `GameMaker` | `.win` `.unx` `.ios` | WORM | ✅ | — | Entries are chunks/<TAG>.bin | [GitHub](https://github.com/UnderminersTeam/UndertaleModTool) |
| Nintendo 3DS GAR | `Gar` | `.gar` | R/W | ✅ | defrag · wipe |  | [3dbrew.org](https://www.3dbrew.org/wiki/GAR) |
| [Game Boy ROM](https://en.wikipedia.org/wiki/Game_Boy) | `Gb` | `.gb` `.gbc` | R | ✅ | — |  | [gbdev.io](https://gbdev.io/pandocs/The_Cartridge_Header.html) |
| LucasArts GOB | `Gob` | `.gob` `.goo` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/luciusDXL/TheForceEngine) |
| Godot PCK | `GodotPck` | `.pck` | R/W | ✅ | defrag · wipe |  | [docs.godotengine.org](https://docs.godotengine.org/en/stable/contributing/development/file_formats/pck.html) |
| Build engine GRP | `Grp` | `.grp` | R/W | ✅ | defrag · wipe |  | [moddingwiki.shikadi.net](https://moddingwiki.shikadi.net/wiki/GRP_Format) |
| Descent HOG | `Hog` | `.hog` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/dxx-rebirth/dxx-rebirth) |
| [Total Annihilation HPI](https://en.wikipedia.org/wiki/Total_Annihilation) | `Hpi` | `.hpi` `.ufo` `.ccx` `.gp3` | R/W | ✅ | defrag · wipe | Unencrypted / zlib subset | [units.tauniverse.com](https://units.tauniverse.com/tutorials/tadesign/tutorials/hpi.htm) |
| LucasArts LFD | `Lfd` | `.lfd` | WORM | ✅ | defrag · wipe | Entries are DATA.<stem> and RMAP.resource | [GitHub](https://github.com/MikeG621/LfdReader) |
| Minecraft region (MCA) | `Mca` | `.mca` `.mcr` | R | ✅ | — |  | [minecraft.wiki](https://minecraft.wiki/w/Region_file_format) |
| Cyan Mohawk | `Mhk` | `.mhk` | WORM | ✅ | defrag · wipe | Entries are typed tDAT_NNNN names | [GitHub](https://github.com/scummvm/scummvm) |
| Westwood MIX | `Mix` | `.mix` | WORM | ✅ | defrag · wipe | Hash-keyed names; hex names are synthesised where the original is absent | [GitHub](https://github.com/OpenRA/OpenRA) |
| [Blizzard MPQ](https://en.wikipedia.org/wiki/MPQ) | `Mpq` | `.mpq` | R/W | ✅ | defrag · wipe |  | [zezula.net](http://www.zezula.net/en/mpq/main.html) |
| Nintendo NARC | `Narc` | `.narc` `.carc` | R/W | ✅ | defrag · wipe |  | [problemkaputt.de](https://problemkaputt.de/gbatek.htm) |
| [Nintendo DS ROM](https://en.wikipedia.org/wiki/Nintendo_DS) | `Nds` | `.nds` | R/W | ✅ | defrag · wipe | NitroFS-oriented output, not ARM boot-code synthesis | [problemkaputt.de](https://problemkaputt.de/gbatek.htm) |
| [NES ROM](https://en.wikipedia.org/wiki/INES) | `Nes` | `.nes` | R | ✅ | — |  | [nesdev.org](https://www.nesdev.org/wiki/INES) |
| NScripter NSA | `Nsa` | `.nsa` | R/W | ✅ | defrag · wipe |  | [nscripter.com](https://www.nscripter.com/) |
| [Quake PAK](https://en.wikipedia.org/wiki/PAK_(file_format)) | `Pak` | `.pak` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/id-Software/Quake) |
| PSP PBP | `Pbp` | `.pbp` | WORM | ✅ | defrag · wipe | Fixed EBOOT section names only | [psdevwiki.com](https://www.psdevwiki.com/psp/PBP) |
| Nintendo Switch PFS0 / NSP | `Pfs0` | `.nsp` `.pfs0` | R/W | ✅ | defrag · wipe |  | [switchbrew.org](https://switchbrew.org/wiki/NCA#PFS0) |
| Sony PSARC | `Psarc` | `.psarc` | R/W | ✅ | defrag · wipe | zlib block path; encrypted and LZMA variants are rejected; names stored lower-case | [psdevwiki.com](https://www.psdevwiki.com/ps3/PlayStation_archive_(PSARC)) |
| [Portable Sound Format](https://en.wikipedia.org/wiki/Portable_Sound_Format) | `Psf` | `.psf` `.psf2` `.minipsf` `.minipsf2` … | WORM | ✅ | defrag |  | [web.archive.org](https://web.archive.org/web/20060212232218/http://wiki.neillcorlett.com/PSFFormat) |
| Nintendo RARC | `Rarc` | `.arc` `.rarc` | WORM | ✅ | defrag · wipe |  | [wiki.cloudmodding.com](https://wiki.cloudmodding.com/zgcn/ARC) |
| RPG Maker RGSSAD | `Rgss` | `.rgssad` `.rgss2a` `.rgss3a` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/morkt/GARbro) |
| Ren'Py RPA | `Rpa` | `.rpa` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/renpy/renpy) |
| NScripter SAR | `Sar` | `.sar` | R/W | ✅ | defrag · wipe | Uncompressed NSA family | [nscripter.com](https://www.nscripter.com/) |
| Nintendo SARC | `Sarc` | `.sarc` `.pack` `.bars` | R/W | ✅ | defrag · wipe | Endian-aware reader; hash-sorted writer | [zeldamods.org](https://zeldamods.org/wiki/SARC) |
| BioWare SFAR | `Sfar` | `.sfar` | WORM | ✅ | wipe | LZX-compressed payload extraction is limited | [GitHub](https://github.com/ME3Tweaks/LegendaryExplorer) |
| Sir-Tech SLF | `Slf` | `.slf` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/ja2-stracciatella/ja2-stracciatella) |
| [SNES ROM](https://en.wikipedia.org/wiki/Super_Nintendo_Entertainment_System) | `Snes` | `.sfc` `.smc` `.fig` `.swc` | R | ✅ | — |  | [snes.nesdev.org](https://snes.nesdev.org/wiki/ROM_header) |
| Mass Effect TFC | `Tfc` | `.tfc` | WORM | ✅ | defrag | Entries are bundle_NNNNN.bin | [GitHub](https://github.com/ME3Tweaks/LegendaryExplorer) |
| Nintendo U8 | `U8` | `.u8` `.arc` | R/W | ✅ | defrag · wipe |  | [wiibrew.org](https://wiibrew.org/wiki/U8_archive) |
| Unreal UMX | `Umx` | `.umx` | WORM | ✅ | defrag · wipe | Header/package shell output only; the export table is not encoded | [wiki.beyondunreal.com](https://wiki.beyondunreal.com/Legacy:Package_File_Format) |
| Unity asset bundle | `UnityBundle` | `.bundle` `.unity3d` `.assetbundle` | WORM | ✅ | defrag · optimize |  | [docs.unity3d.com](https://docs.unity3d.com/Manual/AssetBundlesIntro.html) |
| Unreal .pak | `UnrealPak` | `.pak` | WORM | ✅ | defrag |  | [GitHub](https://github.com/panzi/u4pak) |
| Valve VPK | `Vpk` | `.vpk` | R/W | ✅ | defrag · wipe |  | [developer.valvesoftware.com](https://developer.valvesoftware.com/wiki/VPK) |
| Volition VPP v1 | `Vpp` | `.vpp` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/gibbed/Gibbed.Volition) |
| Volition VPP v2 | `VppV2` | `.vpp_pc` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/gibbed/Gibbed.Volition) |
| [Doom WAD](https://en.wikipedia.org/wiki/Doom_WAD) | `Wad` | `.wad` | R/W | ✅ | defrag · wipe | Lump names are 8 characters | [doomwiki.org](https://doomwiki.org/wiki/WAD) |
| Quake / Half-Life WAD2/WAD3 | `Wad2` | `.wad` | R/W | ✅ | defrag · wipe |  | [developer.valvesoftware.com](https://developer.valvesoftware.com/wiki/WAD) |
| YukaScript YPF | `Ypf` | `.ypf` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/morkt/GARbro) |
| [ZX Spectrum snapshot / tape](https://en.wikipedia.org/wiki/ZX_Spectrum_software) | `ZxSnapshot` | `.sna` `.z80` `.tap` `.tzx` | R | ✅ | — |  | [sinclair.wiki.zxnet.co.uk](https://sinclair.wiki.zxnet.co.uk/wiki/TAP_format) |

### 💾 Backup and disk-image containers

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| [Acronis True Image .tib](https://en.wikipedia.org/wiki/Acronis_True_Image) | `AcronisTib` | `.tib` | R/W | — | — | FileMeta chain and InputItem attribute streams decoded from reverse-engineered evidence | [GitHub](https://github.com/dennisss/acronis-tib) |
| [Acronis .tibx](https://en.wikipedia.org/wiki/Acronis_True_Image) | `AcronisTibx` | `.tibx` | R | ✅ | — | Page-frame walk plus LSM sub-header; record-stream decode is bounded | [acronis.com](https://www.acronis.com) |
| [AFF4](https://en.wikipedia.org/wiki/Advanced_Forensic_Format) | `Aff4` |  | R | ✅ | — |  | [GitHub](https://github.com/aff4/Standard) |
| AOMEI Backupper .adi/.afi | `Aomei` | `.adi` `.afi` | R/W | ✅ | — | BIFH/BIFT and BR header/index structures; no vendor byte-compat claim for own output | [aomeitech.com](https://www.aomeitech.com) |
| [Microsoft NTBackup (MTF)](https://en.wikipedia.org/wiki/NTBackup) | `Bkf` | `.bkf` | R/W | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Microsoft_Tape_Format) |
| EaseUS Todo Backup .pbd | `EaseUsPbd` | `.pbd` | R | ✅ | — | Chunk-stream extraction path | [easeus.com](https://www.easeus.com) |
| [Symantec / Norton Ghost](https://en.wikipedia.org/wiki/Ghost_(disk_utility)) | `Ghost` | `.gho` `.ghs` | R/W | ✅ | — |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Ghost_image) |
| Macrium Reflect X | `Macrium` | `.mrimgx` `.mrbakx` `.mrimg` | WORM | ✅ | — | Open-spec .mrimgx path; entries are disk-image.raw plus block-NN.$* members | [GitHub](https://github.com/macrium/mrimgx_file_layout) |
| Macrium Reflect pre-X | `MacriumPreX` | `.mrimg` `.mrbak` `.mrex` `.mrsql` | R | ✅ | — |  | [macrium.com](https://www.macrium.com) |
| Paragon .pbf | `Paragon` | `.pbf` | R/W | ✅ | — | Own clean-room container path; entries are chunk_NNNNNN.bin, so edits address chunks | [paragon-software.com](https://www.paragon-software.com) |
| [partclone (Clonezilla)](https://en.wikipedia.org/wiki/Clonezilla) | `Partclone` | `.aa` `.img` | R | ✅ | — |  | [partclone.org](https://partclone.org) |
| [Apple Sparsebundle](https://en.wikipedia.org/wiki/Sparse_image) | `Sparsebundle` | `.sparsebundle` | R | ✅ | — |  | [developer.apple.com](https://developer.apple.com/library/archive/documentation/Darwin/Reference/ManPages/man1/hdiutil.1.html) |
| [Apple Sparseimage](https://en.wikipedia.org/wiki/Sparse_image) | `Sparseimage` | `.sparseimage` | R/W | ✅ | — |  | [developer.apple.com](https://developer.apple.com/library/archive/documentation/Darwin/Reference/ManPages/man1/hdiutil.1.html) |
| [Veeam .vbk/.vib/.vrb](https://en.wikipedia.org/wiki/Veeam) | `Veeam` | `.vbk` `.vib` `.vrb` | R | ✅ | — | Summary/trailer path only; the undocumented block layer is not guessed | [GitHub](https://github.com/synacktiv/veeam-velociraptor) |
| VMware VIB | `Vib` | `.vib` | WORM | ✅ | — |  | [blogs.vmware.com](https://blogs.vmware.com/cloud-foundation/2011/09/13/whats-in-a-vib/) |

### 🧩 Executables, resources and other pseudo-archives

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| [PE resources (.rsrc)](https://en.wikipedia.org/wiki/Portable_Executable) | `PeResources` | `.dll` `.exe` `.ocx` `.cpl` … | R | ✅ | — |  | [Microsoft Learn](https://learn.microsoft.com/windows/win32/debug/pe-format) |
| [Resource-only DLL](https://en.wikipedia.org/wiki/Dynamic-link_library) | `ResourceDll` |  | WORM | ✅ | defrag |  | [Microsoft Learn](https://learn.microsoft.com/windows/win32/debug/pe-format) |
| [ELF](https://en.wikipedia.org/wiki/Executable_and_Linkable_Format) | `Elf` | `.elf` `.so` `.o` `.ko` | R | ✅ | — |  | [sco.com](https://www.sco.com/developers/gabi/) |
| [Mach-O](https://en.wikipedia.org/wiki/Mach-O) | `MachO` | `.macho` `.dylib` `.bundle` `.o` | R | ✅ | — |  | [GitHub](https://github.com/apple-oss-distributions/xnu) |
| [DOS MZ executable](https://en.wikipedia.org/wiki/DOS_MZ_executable) | `Mz` | `.exe` `.com` `.ovl` `.bin` | R | ✅ | — |  | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format) |
| [.NET assembly](https://en.wikipedia.org/wiki/.NET_assembly) | `NetAssembly` |  | R | ✅ | — |  | [ecma-international.org](https://ecma-international.org/publications-and-standards/standards/ecma-335/) |
| [WebAssembly module](https://en.wikipedia.org/wiki/WebAssembly) | `Wasm` | `.wasm` | R | ✅ | — |  | [webassembly.github.io](https://webassembly.github.io/spec/core/binary/index.html) |
| [Windows ICO/CUR](https://en.wikipedia.org/wiki/ICO_(file_format)) | `Ico` | `.ico` | R/W | ✅ | defrag |  | [Microsoft Learn](https://learn.microsoft.com/en-us/previous-versions/ms997538(v=msdn.10)) |
| [Windows CUR cursor](https://en.wikipedia.org/wiki/ICO_(file_format)) | `Cur` | `.cur` | WORM | ✅ | defrag |  | [Microsoft Learn](https://learn.microsoft.com/en-us/previous-versions/ms997538(v=msdn.10)) |
| [ANI (animated cursor)](https://en.wikipedia.org/wiki/ANI_(file_format)) | `Ani` | `.ani` | WORM | ✅ | — |  | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/menurc/about-cursors) |
| [TTC](https://en.wikipedia.org/wiki/TrueType#TrueType_Collection) | `Ttc` | `.ttc` | WORM | ✅ | — | Inputs must be .ttf/.otf fonts | [Microsoft Learn](https://learn.microsoft.com/en-us/typography/opentype/spec/) |
| [OTC](https://en.wikipedia.org/wiki/OpenType) | `Otc` | `.otc` | R | ✅ | — |  | [Microsoft Learn](https://learn.microsoft.com/en-us/typography/opentype/spec/) |
| [TTF (per-glyph)](https://en.wikipedia.org/wiki/TrueType) | `Ttf` | `.ttf` | R | ✅ | — |  | [Microsoft Learn](https://learn.microsoft.com/en-us/typography/opentype/spec/) |
| [OTF (per-glyph)](https://en.wikipedia.org/wiki/OpenType) | `Otf` | `.otf` | R | ✅ | — |  | [Microsoft Learn](https://learn.microsoft.com/en-us/typography/opentype/spec/) |
| [gettext .mo](https://en.wikipedia.org/wiki/Gettext) | `Mo` | `.mo` | WORM | ✅ | — | Entries are NNNN_<stem>.txt | [gnu.org](https://www.gnu.org/software/gettext/manual/html_node/MO-Files.html) |
| [gettext .po](https://en.wikipedia.org/wiki/Gettext) | `Po` | `.po` `.pot` | R | ✅ | — |  | [gnu.org](https://www.gnu.org/software/gettext/manual/html_node/PO-Files.html) |
| [AppleSingle](https://en.wikipedia.org/wiki/AppleSingle_and_AppleDouble_formats) | `AppleSingle` | `.as` `.applesingle` | R/W | ✅ | — | Inputs must map to AppleSingle entry ids | [RFC](https://www.rfc-editor.org/rfc/rfc1740) |
| [AppleDouble](https://en.wikipedia.org/wiki/AppleSingle_and_AppleDouble_formats) | `AppleDouble` | `.appledouble` | R/W | ✅ | — | Same body as AppleSingle under the sidecar magic; a data fork is refused, it belongs in the sibling file | [RFC](https://www.rfc-editor.org/rfc/rfc1740) |
| [PKCS #12](https://en.wikipedia.org/wiki/PKCS_12) | `Pkcs12` | `.p12` `.pfx` | R | ✅ | — |  | [RFC](https://www.rfc-editor.org/rfc/rfc7292) |
| [PAR2](https://en.wikipedia.org/wiki/Parchive) | `Par2` | `.par2` | R | ✅ | — |  | [parchive.sourceforge.net](https://parchive.sourceforge.net) |
| [Motorola S-record](https://en.wikipedia.org/wiki/SREC_(file_format)) | `Srec` | `.s19` `.s28` `.s37` `.srec` … | WORM | ✅ | — | Entries are metadata.ini plus firmware.bin | [srecord.sourceforge.net](https://srecord.sourceforge.net) |
| [Windows shell link](https://en.wikipedia.org/wiki/Shortcut_(computing)) | `Lnk` | `.lnk` | WORM | ✅ | — | Entries are header.bin / linkinfo.bin | [Microsoft Learn](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-shllink/) |
| [PCAP](https://en.wikipedia.org/wiki/Pcap) | `Pcap` | `.pcap` `.cap` | R | ✅ | — |  | [tcpdump.org](https://www.tcpdump.org) |
| [PCAPNG](https://en.wikipedia.org/wiki/Pcap) | `Pcapng` | `.pcapng` `.ntar` | R | ✅ | — |  | [GitHub](https://github.com/pcapng/pcapng) |

### 🧪 Scientific, data and CAD containers

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| [Apache Arrow IPC](https://en.wikipedia.org/wiki/Apache_Arrow) | `Arrow` | `.arrow` `.feather` | R | ✅ | — |  | [arrow.apache.org](https://arrow.apache.org/docs/format/Columnar.html) |
| [Apache Avro OCF](https://en.wikipedia.org/wiki/Apache_Avro) | `Avro` | `.avro` | R | ✅ | — |  | [avro.apache.org](https://avro.apache.org/docs/current/specification/) |
| [Collada (.dae)](https://en.wikipedia.org/wiki/COLLADA) | `Collada` | `.dae` | R | ✅ | — |  | [collada.org](http://www.collada.org/2005/11/COLLADASchema) |
| [DICOM](https://en.wikipedia.org/wiki/DICOM) | `Dicom` | `.dcm` `.dicom` | R | ✅ | — |  | [dicom.nema.org](https://dicom.nema.org/medical/dicom/current/output/html/part10.html) |
| [DICOMDIR](https://en.wikipedia.org/wiki/DICOM) | `DicomDir` | `.dcmdir` | R | ✅ | — |  | [dicom.nema.org](https://dicom.nema.org/medical/dicom/current/output/chtml/part10/chapter_8.html) |
| [DXF (AutoCAD Drawing Exchange)](https://en.wikipedia.org/wiki/AutoCAD_DXF) | `Dxf` | `.dxf` | R | ✅ | — |  | [help.autodesk.com](https://help.autodesk.com/view/OARX/2022/ENU/?guid=GUID-235B22E0-A567-4CF6-92D3-38A2306D73F3) |
| [FITS](https://en.wikipedia.org/wiki/FITS) | `Fits` | `.fits` `.fit` `.fts` | WORM | ✅ | defrag · wipe | Entries are hdu_* header/data members | [fits.gsfc.nasa.gov](https://fits.gsfc.nasa.gov) |
| [HDF4](https://en.wikipedia.org/wiki/Hierarchical_Data_Format) | `Hdf4` | `.hdf` `.hdf4` `.h4` | R | ✅ | — |  | [hdfgroup.org](https://www.hdfgroup.org/solutions/hdf4/) |
| [HDF5](https://en.wikipedia.org/wiki/Hierarchical_Data_Format) | `Hdf5` | `.h5` `.hdf5` | R | ✅ | — |  | [GitHub](https://github.com/HDFGroup/hdf5) |
| [Apache Iceberg metadata](https://en.wikipedia.org/wiki/Apache_Iceberg) | `Iceberg` |  | R | ✅ | — |  | [iceberg.apache.org](https://iceberg.apache.org/spec/) |
| [LevelDB SSTable](https://en.wikipedia.org/wiki/LevelDB) | `Leveldb` | `.ldb` `.sst` | R | ✅ | — |  | [GitHub](https://github.com/google/leveldb) |
| [MATLAB MAT v5](https://en.wikipedia.org/wiki/MATLAB) | `Matlab` | `.mat` | R | ✅ | — |  | [mathworks.com](https://www.mathworks.com/help/pdf_doc/matlab/matfile_format.pdf) |
| [MATLAB MAT v4](https://en.wikipedia.org/wiki/MATLAB) | `MatlabV4` | `.mat` | R | ✅ | — |  | [mathworks.com](https://www.mathworks.com/help/pdf_doc/matlab/matfile_format.pdf) |
| [Access MDB / ACCDB](https://en.wikipedia.org/wiki/Microsoft_Access) | `Mdb` | `.mdb` `.accdb` | R | ✅ | — |  | [GitHub](https://github.com/mdbtools/mdbtools) |
| [NetCDF (Classic)](https://en.wikipedia.org/wiki/NetCDF) | `NetCdf` | `.nc` `.cdf` | R | ✅ | — |  | [unidata.ucar.edu](https://www.unidata.ucar.edu/software/netcdf/) |
| [NIfTI](https://en.wikipedia.org/wiki/Neuroimaging_Informatics_Technology_Initiative) | `Nifti` | `.nii` | R | ✅ | — |  | [nifti.nimh.nih.gov](https://nifti.nimh.nih.gov/) |
| [NumPy .npy](https://en.wikipedia.org/wiki/NumPy) | `Npy` | `.npy` | WORM | ✅ | — | Single array: header.bin plus array.bin | [numpy.org](https://numpy.org/doc/stable/reference/generated/numpy.lib.format.html) |
| [NumPy .npz](https://en.wikipedia.org/wiki/NumPy) | `Npz` | `.npz` | WORM | ✅ | wipe | Members carry the .npy suffix | [numpy.org](https://numpy.org/doc/stable/reference/generated/numpy.lib.format.html) |
| [Wavefront OBJ (3D model)](https://en.wikipedia.org/wiki/Wavefront_.obj_file) | `Obj` | `.obj` | R | ✅ | — |  | [paulbourke.net](https://paulbourke.net/dataformats/obj/) |
| [ONNX](https://en.wikipedia.org/wiki/Open_Neural_Network_Exchange) | `Onnx` | `.onnx` | R | ✅ | — |  | [GitHub](https://github.com/onnx/onnx/blob/main/onnx/onnx.proto) |
| [Apache ORC](https://en.wikipedia.org/wiki/Apache_ORC) | `Orc` | `.orc` | R | ✅ | — |  | [orc.apache.org](https://orc.apache.org/specification/) |
| [Apache Parquet](https://en.wikipedia.org/wiki/Apache_Parquet) | `Parquet` | `.parquet` | R | ✅ | — |  | [GitHub](https://github.com/apache/parquet-format) |
| [PLY (Stanford polygon)](https://en.wikipedia.org/wiki/PLY_(file_format)) | `Ply` | `.ply` | R | ✅ | — |  | [paulbourke.net](http://paulbourke.net/dataformats/ply/) |
| [SQLite 3 Database](https://en.wikipedia.org/wiki/SQLite) | `Sqlite` | `.sqlite` `.sqlite3` `.db3` | R | ✅ | — |  | [sqlite.org](https://www.sqlite.org/fileformat2.html) |
| [STL (stereolithography)](https://en.wikipedia.org/wiki/STL_(file_format)) | `Stl` | `.stl` | R | ✅ | — |  | [fabbers.com](https://www.fabbers.com/tech/STL_Format) |
| [Autodesk 3DS](https://en.wikipedia.org/wiki/.3ds) | `Tds` | `.3ds` | R | ✅ | — |  | [paulbourke.net](http://paulbourke.net/dataformats/3ds/) |
| [TFRecord](https://en.wikipedia.org/wiki/TensorFlow) | `TfRecord` | `.tfrecord` `.tfrecords` | WORM | ✅ | defrag · wipe | Entries are record_NNNNN.bin | [tensorflow.org](https://www.tensorflow.org/tutorials/load_data/tfrecord) |
| [Zarr array metadata](https://en.wikipedia.org/wiki/Zarr_(data_format)) | `Zarr` |  | R | ✅ | — |  | [zarr-specs.readthedocs.io](https://zarr-specs.readthedocs.io/) |

### 🎞️ Media containers

| Container | Id | Extensions | Demux | Mux | Remux / edit | Notes | Reference |
| --- | --- | --- | :---: | :---: | :---: | --- | --- |
| [ASF / WMV / WMA](https://en.wikipedia.org/wiki/Advanced_Systems_Format) | `Asf` | `.asf` `.wma` `.wmv` | ✅ | — | — | Header Object children and Data Object walked; no writer | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/wmformat/overview-of-the-asf-format) |
| [AVI](https://en.wikipedia.org/wiki/Audio_Video_Interleave) | `Avi` | `.avi` | ✅ | — | ✅ | movi demux; header chunks can be relocated in place | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/directshow/avi-riff-file-reference) |
| [Bink](https://en.wikipedia.org/wiki/Bink_Video) | `Bik` | `.bik` `.bk2` | ✅ | — | — | Reverse-engineered; audio and video tracks split out, no writer | [MultimediaWiki](https://wiki.multimedia.cx/index.php/Bink_Container) |
| [FLV](https://en.wikipedia.org/wiki/Flash_Video) | `Flv` | `.flv` | ✅ | — | — | AVC re-framed as Annex-B, AAC as ADTS, MP3 raw; other codecs as concatenated frames | [rtmp.veriskope.com](https://rtmp.veriskope.com/pdf/video_file_format_spec_v10_1.pdf) |
| [HLS M3U8](https://en.wikipedia.org/wiki/HTTP_Live_Streaming) | `M3u8` | `.m3u8` `.m3u` | ✅ | — | — | A manifest, not a container: it references segments it does not hold, so there is nothing to mux | [RFC](https://www.rfc-editor.org/rfc/rfc8216) |
| [Matroska / WebM](https://en.wikipedia.org/wiki/Matroska) | `Mkv` | `.mkv` `.webm` `.mka` `.mks` | ✅ | — | ✅ | Tracks, attachments and chapters; Cues can be moved to the front in place | [matroska.org](https://www.matroska.org/technical/elements.html) |
| [MP4 / MOV / 3GP](https://en.wikipedia.org/wiki/MP4_file_format) | `Mp4` | `.mp4` `.m4v` `.m4a` `.mov` … | ✅ | ✅ | ✅ | Track demux; audio-only mux from AAC/PCM inputs; fast-start relayout in place | [ISO](https://www.iso.org/standard/83102.html) |
| [MPEG program stream / VOB](https://en.wikipedia.org/wiki/MPEG_program_stream) | `MpegPs` | `.mpg` `.mpeg` `.vob` `.m2p` … | ✅ | — | — | PES headers stripped; DVD private-stream-1 substreams (AC-3, DTS, LPCM, sub-picture) split | [ISO](https://www.iso.org/standard/75928.html) |
| [MPEG transport stream](https://en.wikipedia.org/wiki/MPEG_transport_stream) | `MpegTs` | `.ts` `.m2ts` `.mts` | ✅ | — | — | Per-PID elementary streams as raw PES | [ISO](https://www.iso.org/standard/75928.html) |
| [RealMedia](https://en.wikipedia.org/wiki/RealMedia) | `RealMedia` | `.rm` `.rmvb` `.ra` | ✅ | — | — | Reverse-engineered; no writer | [MultimediaWiki](https://wiki.multimedia.cx/index.php/RealMedia) |
| [Smacker](https://en.wikipedia.org/wiki/Smacker_video) | `Smk` | `.smk` | ✅ | — | — | Reverse-engineered; no writer | [MultimediaWiki](https://wiki.multimedia.cx/index.php/Smacker) |
| [Blu-ray PGS (.sup)](https://en.wikipedia.org/wiki/Presentation_Graphic_Stream) | `Sup` | `.sup` | ✅ | — | — | A single subtitle stream, not a multi-track container | [GitHub](https://github.com/mjuhasz/BDSup2Sub) |
| [VobSub](https://en.wikipedia.org/wiki/VobSub) | `VobSub` | `.idx` | ✅ | — | — | Index plus one sub-picture stream, not a multi-track container | [sam.zoy.org](http://sam.zoy.org/writings/dvd/subtitles/) |

### 🛡️ Executable packers (descriptors)

| Packer | Id | Extensions | State | Notes | Reference |
| --- | --- | --- | :---: | --- | --- |
| [UPX](https://en.wikipedia.org/wiki/UPX) | `Upx` |  | R | Signature/evidence detection plus in-process NRV payload decompression | [GitHub](https://github.com/upx/upx) |
| [ASPack](https://en.wikipedia.org/wiki/ASPack) | `AsPack` |  | R |  | [aspack.com](http://www.aspack.com) |
| ASProtect | `AsProtect` |  | R |  | [aspack.com](http://www.aspack.com) |
| bzexe | `Bzexe` |  | R |  | [sourceware.org](https://sourceware.org/bzip2/) |
| Crinkler | `Crinkler` |  | R |  | [GitHub](https://github.com/runestubbe/Crinkler) |
| FSG | `Fsg` |  | R |  | [GitHub](https://github.com/horsicq/Detect-It-Easy) |
| GoPacker | `GoPacker` |  | R |  | [GitHub](https://github.com/packing-box/docker-packing-box) |
| [gzexe](https://en.wikipedia.org/wiki/Gzip) | `Gzexe` |  | R |  | [gnu.org](https://www.gnu.org/software/gzip/manual/gzip.html) |
| Huan | `Huan` |  | R |  | [GitHub](https://github.com/frkngksl/Huan) |
| kkrunchy | `Kkrunchy` |  | R |  | [GitHub](https://github.com/farbrausch/fr_public) |
| [LZEXE (DOS exe)](https://en.wikipedia.org/wiki/LZEXE) | `LzExe` |  | R |  | [Archive Team](http://fileformats.archiveteam.org/wiki/LZEXE) |
| MEW | `Mew` |  | R |  | [GitHub](https://github.com/horsicq/Detect-It-Easy) |
| MPRESS | `MPress` |  | R |  | [matcode.com](https://matcode.com/) |
| NsPack | `NsPack` |  | R |  | [GitHub](https://github.com/horsicq/Detect-It-Easy) |
| Origami | `Origami` |  | R |  | [GitHub](https://github.com/dr4k0nia/Origami) |
| Papaw | `Papaw` |  | R |  | [GitHub](https://github.com/dimkr/papaw) |
| PEtite | `Petite` |  | R |  | [un4seen.com](https://www.un4seen.com/petite/) |
| [PKLITE (DOS exe)](https://en.wikipedia.org/wiki/PKLITE) | `PkLite` |  | R |  | [Archive Team](http://fileformats.archiveteam.org/wiki/PKLITE) |
| Shrinkler | `Shrinkler` |  | R |  | [GitHub](https://github.com/askeksa/Shrinkler) |
| Silent_Packer | `SilentPacker` |  | R |  | [GitHub](https://github.com/SilentVoid13/Silent_Packer) |
| [Themida](https://en.wikipedia.org/wiki/Themida) | `Themida` |  | R |  | [oreans.com](https://www.oreans.com/themida.php) |
| [VMProtect](https://en.wikipedia.org/wiki/VMProtect) | `VmProtect` |  | R |  | [vmpsoft.com](https://vmpsoft.com) |
| Yoda's Crypter | `YodaCrypter` |  | R |  | [sourceforge.net](https://sourceforge.net/projects/yodap/) |

### 🛠️ Executable packer handlers

`FileFormat.ExePackers` and `FileFormat.Upx` carry the packer descriptors above plus the `IExecutablePackerHandler` implementations that detect a packer, locate its payload and, where the format is understood, inflate it with the package's own building blocks. Levels: **Unpack** — payload located and decompressed to a memory image (a byte-identical pre-packing file is generally unreachable because packers rebuild imports, relocations and resources); **Locate** — packer recognised and its payload emitted, decompression not yet wired; **Detect** — recognition and diagnostics only (runtime protectors).

| Packer | Level | Core / notes |
| --- | --- | --- |
| UPX | Unpack | NRV2B/D/E and LZMA cores; full detect → decompress → memory image → synthetic rebuild. LZMA-mode payloads (method 14) are located and reported, not decoded. |
| ASPack | Unpack | Own LZ77 + Huffman core (`AsPackLzDecoder`), not aPLib. Region table drives an in-place restore of every packed section; the E8/E9 call filter is reversed. |
| BeRoEXEPacker | Unpack | Entry stub parsed for its immediates; LZMA (129 of 130 samples) or aPLib body decoded, E8/E9 filter reversed, `reconstructed.exe` emitted. |
| Eronana Packer | Unpack | Static LZ77 + canonical-Huffman decoder validated byte-for-byte against a real sample. |
| Enigma Virtual Box | Unpack* | `.enigma1`/`.enigma2` recognised; sampled corpus inflates through the managed aPLib path. Bundled file-tree extraction remains. |
| MEW | Unpack* | Section layout recognised; managed generic payload recovery emits `reconstructed.exe`; other variants fall back to payload location. |
| Molebox | Unpack | 2.x loader chain replayed: LCG keystream over an LZSS'd loader blob, IDEA-protected configuration, per-section IDEA + zlib. All 415 recoverable sections in the corpus come back byte-identical. |
| MPRESS | Unpack | 2.x: bare LZMA1 stream behind MPRESS's own 8-byte header, decoded through `BB_Lzma`; E8/E9 transform reversed. 1.x packs with another codec and stays at payload location. |
| Packman | Unpack | Shared aPLib PE pipeline; decompressed payload plus synthetic rebuilt PE. |
| PEtite | Unpack | Block table behind the entry stub replayed; every block inflated with the PEtite DEFLATE dialect; absolute-branch transform reversed. Imports, relocations and OEP are not rebuilt. |
| RLPack | Unpack | Own `{sourceRva, destinationRva}` block table, one bare LZMA or aPLib stream per section; x86 call/jump filter reversed. All 130 corpus samples decompress. |
| WinUpack | Unpack | Upack's LZMA-idiom range coder plus call/jump filter, driven by the loader's parameter block; both container shapes decode. |
| Yoda's Crypter | Unpack | Stub walker replays the per-build byte cipher and restores the original entry point; 129 of 130 corpus samples decrypt. |
| GZEXE / BZEXE | Unpack | Shell wrappers with embedded gzip / bzip2 payload; the original executable is restored statically. |
| Papaw | Unpack | ELF wrapper with obfuscated XZ/LZMA2 payload; appended original restored. |
| GoPacker | Unpack | Appended Zstandard executable payload restored. |
| Origami | Unpack | .NET wrapper with XORed raw-Deflate managed payload; original assembly restored. |
| PyPePacker | Unpack | Python zipapp PE wrapper; EntropyEncoding v2, RC6-CBC and gzip reversed. |
| PE-Toy | Unpack | `.petoy` shell section with aPLib payload through the shared aPLib PE pipeline. |
| Silent_Packer | Unpack | ELF64 XOR section-insertion wrapper; `.text` and entry point restored for the supported variant. |
| Huan | Unpack | PE64 loader with encrypted `.huan` section; embedded PE decrypted. |
| hXOR-Packer | Unpack | Stored and single-byte-XOR transforms reversed byte-for-byte; the bespoke-Huffman modes stay at payload location. |
| Xor_Packer | Unpack | .NET wrapper with Base64/XOR/Base64 settings; embedded PE decoded. |
| Alternate EXE Packer | Unpack | A UPX 3.96 front end; routed through the UPX pipeline rather than a duplicate detector. |
| _(generic aPLib PE)_ | Unpack | `aplib_pe` fallback: any PE whose section inflates to a clean aPLib stream. |
| _(generic NRV PE)_ | Unpack | `nrv_pe` fallback: any PE whose section inflates as NRV2B/2D/2E to a plausible payload. |
| FSG | Locate* | `FSG!` marker and t/ta/a layouts recognised; synthetic aPLib-FSG fixtures unpack through the generic path. |
| PECompact | Locate | Payload found and framed; the region is a series of compressed blocks (block sizes at offset 6), not one stream, so no codec is guessed. |
| NsPack | Locate | `nsp0`/`nsp1` layout; `nsp1` opens with the relocated resource directory, then a second-stage loader, then the compressed data — the table has to be read before a decoder is written. |
| Neolite | Locate* | Ordinary section names; `.text` opens with the loader and no section is dense enough to be a single compressed image. |
| JDPack / Exe32pack / eXpressor / Alienyze | Locate | Packer section emitted as `compressed_payload.bin`; custom LZ recovery remains. |
| Amber | Locate | Reflective PE loader; a plaintext embedded PE is carved when present, the XOR/RC4-obscured payload is located otherwise. |
| SimpleDpack | Locate | `.dpack` blob plus stripped-section targets emitted; the published release did not match its documented LZMA container, so nothing is decoded against an unconfirmed format. |
| PE-Packer (czs108) | Locate | `.shell` section emitted; the +0xCC cipher and import rewrite are documented but their byte ranges live in a MASM-compiled shell with no reference binary. |
| squishy | Locate | `logicoma` section and credit text recognised against real 0.1.3/0.2.0 output; closed-source context-mixing payload is not decoded. |
| Themida / WinLicense, TELock, Yoda's Protector | Detect / Locate | Runtime protectors: the protected body is emitted as `protected_section_*.bin`; no decompression is claimed. Yoda's Protector's cipher and LZO1X stream are understood, the section-name restore is not. |
| Crinkler, kkrunchy, Shrinkler | Detect | Demoscene compressing linkers with undocumented context-mixing payloads; metadata and diagnostics only. |

Measured against the [chesvectain/PackingData](https://github.com/chesvectain/PackingData) corpus (130 samples per packer): recognition 2455 of 2470; of the 1562 samples with a pre-packing original, 1300 come back with a distinctive 32-byte run of that original in the recovered body. Per-packer counts and the analysis of the still-blocked packers are in [`docs/EXE-PACKER-NOTES.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/EXE-PACKER-NOTES.md). The Packing Box manifest audit (`DatasetProbe.PackingBoxPackersManifest_IsFetchableAndAuditsRegisteredHandlers`) reports which of its 104 packer entries have no handler yet.

### 🔗 Compound formats

`tar.gz`, `tar.bz2`, `tar.xz`, `tar.zst`, `tar.lz4`, `tar.lz` and `tar.br` are composed from the TAR descriptor and the matching stream descriptor (`CanCompoundWithTar`). Detection and writing reuse those two layers; there is no second TAR implementation.

### 🚧 Gaps

- **Media containers.** ASF, Matroska, AVI, MPEG-TS, MPEG-PS, FLV, RealMedia, Smacker and Bink demux only; MP4 muxes audio tracks only. This is a limit of what demuxing preserves, not of the container specs. The demuxers hand back each track as a codec elementary stream — AVC re-framed as Annex-B, AAC as ADTS, per-PID PES — and per-frame timestamps, interleaving order and the index live in the container layer that is dropped on the way out. Muxing those entries back would mean inventing presentation timing rather than restoring it, so the writers are not there. Closing this needs a demux surface that carries timed packets, not another writer. Bink, Smacker, RealMedia and ASF are additionally reverse-engineered rather than specified. M3U8, PGS `.sup` and VobSub are not container muxes at all: a playlist is a manifest referencing segments it does not contain, and the two subtitle formats are single streams. ISO-BMFF brands are parsed generically, without a brand registry.
- **Whole-image and typed-input writers** (Amiga disk archivers, DMS, sparse images, PBP, ICO/CUR/ANI, TTC, AppleSingle/AppleDouble, Wrapster, OVA) create only what their format can hold; arbitrary file trees are refused with a message rather than mangled.
- **Reverse-engineered backup formats** (Acronis, AOMEI, EaseUS, Macrium, Paragon, Veeam) are decoded to the depth the evidence supports; unknown encrypted or index layers stay unknown.
- **Executable packers** blocked at Locate are listed above with the reason; the manifest audit names the unmapped ones.

## 🚀 Quick start

### Detect and list an archive

```csharp
using Compression.Registry;

using var input = File.OpenRead("payload.tar.gz");
var archive = FormatRegistry.DetectArchiveOperations(input);
foreach (var entry in archive.List(input))
  Console.WriteLine($"{entry.Name,-40} {entry.Size,12:N0}");
```

### Round-trip a compression stream

```csharp
using FileFormat.Brotli;

byte[] original = File.ReadAllBytes("page.html");
var format = new BrotliFormatDescriptor();
byte[] compressed = format.Compress(original);
byte[] restored = format.Decompress(compressed);
```

### Demux a media container

```csharp
using FileFormat.MpegPs;

using var vob = File.OpenRead("VTS_01_1.VOB");
var demuxer = new MpegPsFormatDescriptor();
demuxer.Extract(vob, "out", password: null, files: null); // stream_E0_mpeg2video.m2v, stream_BD_80_ac3.ac3, …
```

## 🏗️ Architecture

### Archive state model

A descriptor advertises what it can do twice, and the two must agree: a `FormatCapabilities` bit for quick gating, and the interface that carries the method the orchestrator calls.

| Implement… | …and the format gains |
| --- | --- |
| `IArchiveFormatOperations` | List / Extract / Test — **R** |
| `IArchiveCreatable` | Create — **WORM**; override `CreateFromStreams` for OOM-free creation |
| `IArchiveModifiable` | Add / Replace / Remove and purge (remove all) — **R/W**. The default implementation is the verified extract → edit → re-create rebuild; formats with a cheaper native editor override it |
| `IArchiveDefragmentable` | defrag |
| `IArchiveShrinkable` | shrink |
| `IWipeEmpty` / `IArchiveLayoutMap` | wipe (zero proven-dead gaps; the layout map also feeds the block-map preview) |
| `ILayoutOptimizable` | optimize |
| `IFileInternalLayoutMap` / `IFileInternalChunkMover` | reorder container metadata in place |
| `IStreamFormatOperations` | single-stream compress / decompress with `FormatCreateOptions` tunables |

`CanModify` is withheld from create-only formats whose checksum chain an append would break (WIM, split WIM) and from writers that reject an arbitrary edited member set (Wrapster, OVA), even though the rebuild machinery could run; `WriteCapabilityHonestyTests` enforces that every `CanModify` claimant implements `IArchiveModifiable`, and `ArchiveModifyRoundTripTests` proves the edit round-trips.

The full model — tiers, archive vs. pseudo-archive, the five maintenance verbs and the composite `compact`, the block-map display contract and the streaming paths — is specified in [`docs/ARCHIVE-MODEL.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/ARCHIVE-MODEL.md). Per-verb coverage of the filesystem descriptors lives in [`docs/OPERATION_COVERAGE.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/OPERATION_COVERAGE.md); for the archive descriptors it is the Maintenance column above.

### On-disk derivations

Two codecs this package writes have no published specification. What was measured to make them interoperate is written up so it is not lost: [`docs/LZMS-ON-DISK.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/LZMS-ON-DISK.md) (WIM LZMS resources, verified by `wimlib-imagex verify`) and [`docs/QUANTUM-ON-DISK.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/QUANTUM-ON-DISK.md) (CAB Quantum folders, verified by `cabextract`). The BitRock installer layout is documented beside its reader in [`FileFormats/FileFormat.BitRock/FORMAT-NOTES.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/FileFormats/FileFormat.BitRock/FORMAT-NOTES.md). Size ceilings of the underlying building blocks are measured in [`docs/LARGE-INPUTS.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/LARGE-INPUTS.md).

## 🧭 When to use this package

Use it when a .NET process needs to enumerate, extract, test, create, edit or inspect a broad range of archives, packages, images and streams without native archive libraries. If all you need is ordinary ZIP at default settings, `System.IO.Compression.ZipArchive` is simpler. Original-vendor encoder parity for proprietary formats is not implied: readable, writable, interoperable output is the goal.

## 📚 API reference

<!-- API:BEGIN generated by Hawkynt/RepositoryTemplate/package-readme — edit the XML docs in source, not here -->

Every public and protected member of all 1133 types, generated from the built assembly and its XML documentation, is in [REFERENCE.md](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.Archives/REFERENCE.md).

<!-- API:END -->

## 🔌 Dependencies

| Dependency | Role |
| --- | --- |
| [`Hawkynt.Compression.Core`](https://www.nuget.org/packages/Hawkynt.Compression.Core/) | Shared compression, entropy, transform, bit-I/O and registry primitives |
| Native archive/compression libraries | **None required at runtime.** |

## ⚠️ Limitations

- WORM is not R/W: creating a valid archive is different from safely editing one, and only descriptors with a proven edit path advertise `CanModify`.
- R/W by rebuild rewrites the container; formats whose listing renames entries (track images, chunked backup images, hash-keyed game archives) can only address entries by the names they list.
- LZFSE: uncompressed and LZVN blocks only. ZPAQ: no ZPAQL virtual machine. StuffIt X and UMX writers emit the envelope shell only. SFAR: LZX payload extraction is limited. Inno Setup: some versions expose no per-file extraction.
- OLE2 (DOC / XLS / PPT / MSG / Thumbs.db / MSI) creation produces a valid CFB envelope, not the application's document or database streams.
- RAR and 7z creation target the implemented RAR4/RAR5 and 7z paths, not every historical writer version, and no vendor encoder heuristic is reproduced.
- Media containers are demuxed at the container level; carried codecs are decoded only where the audio package provides them.
- MPEG-TS elementary streams are emitted as raw PES; MPEG-PS strips PES headers. PyInstaller onefile builds for Linux are detected as ELF by the stronger magic.
- Installer and package parsing is inspection only; no install logic or script is executed.
- Reverse-engineered proprietary structures are documented only to the depth evidenced by code, tests and reference binaries. Unknown structure is not filled with guesses.

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
