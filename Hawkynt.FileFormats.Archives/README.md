Warning: truncated output (original token count: 20643)
Total output lines: 582

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

- Compression-stream readers and writers for modern and historical formats, including the encodings (BinHex, MacBinary, uuencode/base64, yEnc).
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
| aPLib | `ApLib` | `.aplib` | ✅ | ✅ | ✅ | Standard 24-byte AP32 wrapper around a bare aPLib stream; older self-framed streams still read | [ibsensoftware.com](https://ibsensoftware.com/products_aPLib.html) |
| BALZ | `Balz` | `.balz` | ✅ | ✅ | ✅ | Flexible look-ahead parser; optimal mode keeps the greedy stream when it is smaller | [sourceforge.net](https://sourceforge.net/projects/balz/) |
| Base64 (uuencode wrapper) | `B64Encoding` | `.b64` `.base64` | ✅ | ✅ | — | libarchive-compatible `begin-base64` / `====` wrapper; not bare RFC 4648 Base64 | [GitHub](https://github.com/libarchive/libarchive/blob/master/libarchive/archive_write_add_filter_b64encode.c) |
| BCM | `Bcm` | `.bcm` | ✅ | ✅ | ✅ | Block-size search (16–128 KiB) | [GitHub](https://github.com/encode84/bcm) |
| [BinHex](https://en.wikipedia.org/wiki/BinHex) | `BinHex` | `.hqx` | ✅ | ✅ | — |  | [RFC](https://www.rfc-editor.org/rfc/rfc1741) |
| BriefLZ | `BriefLz` | `.blz` | ✅ | ✅ | ✅ | Reference-compatible blzpack stream; optimizer compares managed effort levels 1–10 | [GitHub](https://github.com/jibsen/brieflz) |
| [Brotli](https://en.wikipedia.org/wiki/Brotli) | `Brotli` | `.br` | ✅ | ✅ | ✅ |  | [RFC](https://www.rfc-editor.org/rfc/rfc7932) |
| BSC | `Bsc` | `.bsc` | ✅ | ✅ | ✅ | Managed BWT+MTF+RLE payload; optimizer searches block size/context order; full libbsc QLFC/LZP parity remains open | [GitHub](https://github.com/IlyaGrebnov/libbsc) |
| [bzip2](https://en.wikipedia.org/wiki/Bzip2) | `Bzip2` | `.bz2` `.bzip2` | ✅ | ✅ | ✅ |  | [sourceware.org](https://sourceware.org/bzip2/manual/manual.html) |
| cmix | `Cmix` | `.cmix` | ✅ | ✅ | ✅ |  | [GitHub](https://github.com/byronknoll/cmix) |
| [Unix compress (.Z)](https://en.wikipedia.org/wiki/Compress_(software)) | `Compress` | `.z` | ✅ | ✅ | ✅ |  | [pubs.opengroup.org](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/compress.html) |
| CP/M Crunch | `Crunch` | `.cru` | ✅ | ✅ | ✅ |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Crunch) |
| CSC | `Csc` | `.csc` | ✅ | ✅ | ✅ |  | [GitHub](https://github.com/fusiyuan2010/CSC) |
| Density | `Density` | `.density` | ✅ | ✅ | ✅ |  | [GitHub](https://github.com/k0dai/density) |
| Freeze | `Freeze` | `.f` `.freeze` | ✅ | ✅ | ✅ | Optimizer searches the parse strategy, the match-search depth and the position Huffman table | [Archive Team](http://fileformats.archiveteam.org/wiki/Freeze) |
| [gzip](https://en.wikipedia.org/wiki/Gzip) | `Gzip` | `.gz` `.gzip` | ✅ | ✅ | ✅ |  | [RFC](https://www.rfc-editor.org/rfc/rfc1952) |
| ICE Packer | `IcePacker` | `.ice` | ✅ | ✅ | ✅ |  | [Archive Team](http://fileformats.archiveteam.org/wiki/ICE) |
| KWAJ | `Kwaj` |  | ✅ | ✅ | ✅ |  | [Archive Team](http://fileformats.archiveteam.org/wiki/KWAJ) |
| Lizard (LZ5) | `Lizard` | `.liz` | ✅ | ✅ | ✅ |  | [GitHub](https://github.com/inikep/lizard) |
| [LZ4 frame](https://en.wikipedia.org/wiki/LZ4_(compression_algorithm)) | `Lz4` | `.lz4` | ✅ | ✅ | ✅ |  | [GitHub](https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md) |
| [LZFSE](https://en.wikipedia.org/wiki/LZFSE) | `Lzfse` | `.lzfse` | ✅ | ✅ | — | Uncompressed and LZVN blocks only; the FSE/tANS compressed block families are not implemented | [GitHub](https://github.com/lzfse/lzfse) |
| LZG | `Lzg` | `.lzg` | ✅ | ✅ | ✅ |  | [GitHub](https://github.com/mbitsnbites/liblzg) |
| LZHAM | `Lzham` | `.lzham` | ✅ | ✅ | ✅ |  | [GitHub](https://github.com/richgel999/lzham_codec) |
| [Lzip](https://en.wikipedia.org/wiki/Lzip) | `Lzip` | `.lz` `.lzip` | ✅ | ✅ | ✅ |  | [nongnu.org](https://www.nongnu.org/lzip/manual/lzip_manual.html#File-format) |
| [LZMA (.lzma)](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Markov_chain_algorithm) | `Lzma` | `.lzma` | ✅ | ✅ | ✅ |  | [7-zip.org](https://www.7-zip.org/sdk.html) |
| [lzop](https://en.wikipedia.org/wiki/Lzop) | `Lzop` | `.lzo` | ✅ | ✅ | ✅ |  | [lzop.org](https://www.lzop.org/) |
| [LZS](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Stac) | `Lzs` | `.lzs` | ✅ | ✅ | ✅ |  | [RFC](https://www.rfc-editor.org/rfc/rfc2395) |
| [MacBinary](https://en.wikipedia.org/wiki/MacBinary) | `MacBinary` | `.bin` `.macbin` | ✅ | ✅ | ✅ |  | [RFC](https://www.rfc-editor.org/rfc/rfc1740) |
| MCM | `Mcm` | `.mcm` | ✅ | ✅ | ✅ | Optimizer searches Legacy plus reduced Turbo/Fast/Mid/High/Max managed profiles | [GitHub](https://github.com/mathieuchartier/mcm) |
| [PackBits](https://en.wikipedia.org/wiki/PackBits) | `PackBits` | `.packbits` | ✅ | ✅ | ✅ |  | [developer.apple.com](https://developer.apple.com/library/archive/documentation/mac/pdf/MoreMacintoshToolbox.pdf) |
| [PAQ8](https://en.wikipedia.org/wiki/PAQ) | `Paq8` | `.paq8l` `.paq8` | ✅ | ✅ | ✅ |  | [mattmahoney.net](https://mattmahoney.net/dc/paq.html) |
| PowerPacker | `PowerPacker` | `.pp` `.pp20` | ✅ | ✅ | ✅ |  | [Archive Team](http://fileformats.archiveteam.org/wiki/PowerPacker) |
| [PPMd](https://en.wikipedia.org/wiki/Prediction_by_partial_matching) | `Ppmd` | `.pmd` | ✅ | ✅ | ✅ |  | [7-zip.org](https://www.7-zip.org/sdk.html) |
| QuickLZ | `QuickLz` | `.quicklz` | ✅ | ✅ | ✅ | Level 1 and level 3; the optimizer searches the level and the level-3 search depth | [quicklz.com](http://www.quicklz.com/) |
| RefPack / QFS | `RefPack` | `.qfs` `.refpack` | ✅ | ✅ | ✅ | Optimizer searches the history window, the match-search depth and quick match indexing | [wiki.niotso.org](http://wiki.niotso.org/RefPack) |
| RNC ProPack | `Rnc` | `.rnc` | ✅ | ✅ | ✅ |  | [segaretro.org](https://segaretro.org/Rob_Northen_compression) |
| [rzip](https://en.wikipedia.org/wiki/Rzip) | `Rzip` | `.rz` `.rzip` | ✅ | ✅ | ✅ |  | [rzip.samba.org](https://rzip.samba.org/) |
| [Snappy](https://en.wikipedia.org/wiki/Snappy) | `Snappy` | `.sz` `.snappy` | ✅ | ✅ | ✅ |  | [GitHub](https://github.com/google/snappy/blob/main/framing_format.txt) |
| Squeeze (SQ) | `Squeeze` | `.sqz` | ✅ | ✅ | ✅ |  | [Archive Team](http://fileformats.archiveteam.org/wiki/SQ) |
| [SWF](https://en.wikipedia.org/wiki/SWF) | `Swf` | `.swf` | ✅ | ✅ | ✅ | FWS/CWS/ZWS envelope optimizer; no tag-level parsing | [open-flash.github.io](https://open-flash.github.io/mirrors/swf-spec-19.pdf) |
| SZ (MS COMPRESS, KWAJ-less) | `SzCompress` |  | ✅ | ✅ | ✅ |  | [Archive Team](http://fileformats.archiveteam.org/wiki/SZDD) |
| SZDD | `Szdd` |  | ✅ | ✅ | ✅ |  | [Archive Team](http://fileformats.archiveteam.org/wiki/SZDD) |
| [uuencode](https://en.wikipedia.org/wiki/Uuencoding) | `UuEncoding` | `.uue` `.uu` | ✅ | ✅ | — |  | [pubs.opengroup.org](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/uuencode.html) |
| [XZ](https://en.wikipedia.org/wiki/XZ_Utils) | `Xz` | `.xz` | ✅ | ✅ | ✅ |  | [tukaani.org](https://tukaani.org/xz/xz-file-format.txt) |
| [yEnc](https://en.wikipedia.org/wiki/YEnc) | `YEnc` | `.yenc` `.uu` | ✅ | ✅ | — |  | [yenc.org](http://www.yenc.org/yenc-draft.1.3.txt) |
| Yaz0 | `Yaz0` | `.yaz0` `.szs` | ✅ | ✅ | ✅ |  | [wiki.tockdom.com](https://wiki.tockdom.com/wiki/YAZ0) |
| [zlib](https://en.wikipedia.org/wiki/Zlib) | `Zlib` | `.zlib` | ✅ | ✅ | ✅ |  | [RFC](https://www.rfc-editor.org/rfc/rfc1950) |
| Zling | `Zling` | `.zling` | ✅ | ✅ | ✅ |  | [GitHub](https://github.com/richox/libzling) |
| [Zstandard](https://en.wikipedia.org/wiki/Zstd) | `Zstd` | `.zst` `.zstd` | ✅ | ✅ | ✅ |  | [RFC](https://www.rfc-editor.org/rfc/rfc8878) |

### 🗜️ Archive containers

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| [ACE](https://en.wikipedia.org/wiki/ACE_(compressed_file_format)) | `Ace` | `.ace` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/droe/acefile) |
| [afio](https://en.wikipedia.org/wiki/Afio) | `Afio` | `.afio` | WORM | ✅ | — | Writes stored members only; the per-file gzip extension is read but not written | [GitHub](https://github.com/kholtman/afio) |
| [ALZip](https://en.wikipedia.org/wiki/ALZip) | `AlZip` | `.alz` | R/W | ✅ | defrag · wipe |  | [kippler.com](http://www.kippler.com/win/unalz/) |
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
| [CPIO](https://en.wikipedia.org/wiki/Cpio) | `Cpio` | `.cpio` | R/W | ✅ | defrag · wipe | Reads and writes all four header variants — binary (both byte orders), `odc`, `newc` and `crc`, the last with its payload checksum verified; the `Format` option picks one and edits keep the archive's own | [pubs.opengroup.org](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html) |
| [DAR (Disk ARchive)](https://en.wikipedia.org/wiki/Dar_(disk_archiver)) | `Dar` | `.dar` | R | ✅ | — |  | [dar.linux.free.fr](http://dar.linux.free.fr) |
| DCS (Amiga) | `Dcs` | `.dcs` | WORM | ✅ | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| [DiskDoubler](https://en.wikipedia.org/wiki/DiskDoubler) | `DiskDoubler` | `.dd` `.sea` | WORM | — | wipe | Single-fork compressor: one payload per file | [GitHub](https://github.com/MacPaw/XADMaster) |
| [DMS](https://en.wikipedia.org/wiki/Disk_Masher_System) | `Dms` | `.dms` | WORM | ✅ | defrag |  | [GitHub](https://github.com/markrabjohn/xDMS) |
| [EGG (ALZip)](https://en.wikipedia.org/wiki/EGG_(file_format)) | `Egg` | `.egg` | WORM | ✅ | defrag |  | [GitHub](https://github.com/alkegi/docs/blob/master/egg.md) |
| [ESD](https://en.wikipedia.org/wiki/Windows_Imaging_Format) | `Esd` | `.esd` | WORM | ✅ | wipe | Solid LZMS WIM; created images carry a metadata resource but entries re-list as resources | [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wim-and-esd-windows-image-files-overview) |
| [FreeArc](https://en.wikipedia.org/wiki/FreeArc) | `FreeArc` | `.arc` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/Bulat-Ziganshin/FA) |
| HA | `Ha` | `.ha` | R/W | ✅ | defrag · wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/HA) |
| [IFF CDAF](https://en.wikipedia.org/wiki/Interchange_File_Format) | `IffCdaf` | `.cdaf` | R/W | ✅ | defrag · wipe |  | [Aminet](https://aminet.net) |
| [LBR](https://en.wikipedia.org/wiki/LBR_(file_format)) | `Lbr` | `.lbr` | R/W | ✅ | defrag · wipe |  | [gaby.de](http://www.gaby.de/cpm/manuals/archive/lbr.txt) |
| LhF (LhFloppy) | `LhF` | `.lhf` | R/W | ✅ | defrag · wipe | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| [lrzip](https://en.wikipedia.org/wiki/Rzip#lrzip) | `Lrzip` | `.lrz` | WORM | ✅ | defrag | LZMA-wrapped subtype only; other lrzip subtypes are rejected; single data member | [GitHub](https://github.com/ckolivas/lrzip) |
| Lynx (Commodore) | `Lynx` | `.lnx` | R/W | ✅ | defrag · shrink · wipe | Stored entries only | [Archive Team](http://fileformats.archiveteam.org/wiki/Lynx_(Commodore_64)) |
| [LHA / LZH](https://en.wikipedia.org/wiki/LHA_(file_format)) | `Lzh` | `.lzh` `.lha` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/jca02266/lha) |
| [LZX (Amiga)](https://en.wikipedia.org/wiki/LZX) | `LzxAmiga` | `.lzx` | R/W | ✅ | defrag · wipe |  | [Aminet](https://aminet.net) |
| [mtree](https://man.freebsd.org/cgi/man.cgi?query=mtree&sektion=5) | `Mtree` | `.mtree` | WORM | ✅ | — | Filesystem metadata manifest; does not embed file bodies, so CWB does not dereference `contents=` host paths | [FreeBSD mtree(5)](https://man.freebsd.org/cgi/man.cgi?query=mtree&sektion=5) |
| [NuFX / ShrinkIt](https://en.wikipedia.org/wiki/ShrinkIt) | `NuFx` | `.shk` `.sdk` `.bxy` | R/W | ✅ | defrag · shrink · wipe |  | [nulib.com](https://nulib.com/library/FTN.e08002.htm) |
| PackDisk (Amiga) | `PackDisk` | `.pdsk` | WORM | ✅ | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| PackIt | `PackIt` | `.pit` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/MacPaw/XADMaster) |
| [RAR](https://en.wikipedia.org/wiki/RAR_(file_format)) | `Rar` | `.rar` | R/W | ✅ | wipe | v1–v5 readers; creation and edits emit RAR4/RAR5 without claiming WinRAR encoder parity | [rarlab.com](https://www.rarlab.com/technote.htm) |
| [7z](https://en.wikipedia.org/wiki/7z) | `SevenZip` | `.7z` | R/W | ✅ | defrag · wipe |  | [7-zip.org](https://www.7-zip.org/7z.html) |
| [SHAR](https://en.wikipedia.org/wiki/Shar) | `Shar` | `.shar` `.sh` | R/W | ✅ | defrag | Add appends in place; Remove re-emits the script from the survivors | [gnu.org](https://www.gnu.org/software/sharutils/) |
| [Spark (RISC OS)](https://en.wikipedia.org/wiki/ARC_(file_format)) | `Spark` | `.spk` `.spark` | R/W | ✅ | defrag · wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/Spark) |
| [Split File (.001)](https://en.wikipedia.org/wiki/File_spanning) | `SplitFile` | `.001` | WORM | ✅ | — |  | [Wikipedia](https://en.wikipedia.org/wiki/File_spanning) |
| [SQX](https://en.wikipedia.org/wiki/SQX) | `Sqx` | `.sqx` | R/W | ✅ | defrag · wipe |  | [encode.su](https://encode.su/threads/1290-SQX-(by-SpeedProject)) |
| [StuffIt](https://en.wikipedia.org/wiki/StuffIt) | `StuffIt` | `.sit` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/MacPaw/XADMaster) |
| [StuffIt X](https://en.wikipedia.org/wiki/StuffIt) | `StuffItX` | `.sitx` | WORM | ✅ | wipe | Writer emits the envelope shell only; the proprietary element catalog is not synthesised | [GitHub](https://github.com/MacPaw/XADMaster) |
| [Split WIM (.swm)](https://en.wikipedia.org/wiki/Windows_Imaging_Format) | `Swm` | `.swm` `.swm2` `.swm3` `.swm4` … | R | ✅ | wipe |  | [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wim-and-esd-windows-image-files-overview) |
| [T64 (Commodore tape image)](https://en.wikipedia.org/wiki/T64_(file_format)) | `T64` | `.t64` | R/W | ✅ | defrag · wipe |  | [vice-emu.sourceforge.net](https://vice-emu.sourceforge.io/) |
| [TAR](https://en.wikipedia.org/wiki/Tar_(computing)) | `Tar` | `.tar` | R/W | ✅ | defrag · shrink · wipe |  | [pubs.opengroup.org](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html) |
| UHARC | `Uharc` | `.uha` | R/W | ✅ | defrag · wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/UHARC) |
| [WIM](https://en.wikipedia.org/wiki/Windows_Imaging_Format) | `Wim` | `.wim` `.swm` `.esd` | WORM | ✅ | wipe | LZX / XPRESS / LZMS paths; kept create-only because an append edit would break the checksum chain | [wimlib.net](https://wimlib.net/) |
| Wrapster | `Wrapster` |  | WORM | ✅ | defrag · wipe | MP3-wrapper archive carrying one member; stays WORM by design | [Archive Team](http://fileformats.archiveteam.org/wiki/Wrapster) |
| [XAR](https://en.wikipedia.org/wiki/Xar_(archiver)) | `Xar` | `.xar` | R/W | ✅ | defrag · wipe |  | [GitHub](https://github.com/mackyle/xar) |
| xDisk / GDC (Amiga) | `xDisk` | `.xdsk` `.gdc` | WORM | ✅ | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| xMash (Amiga) | `xMash` | `.xmsh` | WORM | ✅ | defrag | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net) |
| ZAP (Amiga) | `Zap` | `.zap` | WORM | ✅ | wipe | Whole-disk archiver: entries are track_NNN.raw | [Aminet](https://aminet.net/) |
| [ZIP](https://en.wikipedia.org/wiki/ZIP_(file_format)) | `Zip` | `.zip` `.zipx` | R/W | ✅ | defrag · shrink · wipe · optimize | Store, Deflate, Deflate64, Shrink, Reduce, Implode, BZip2, LZMA, PPMd, Zstd, AES | [pkware.cachefly.net](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT) |
| [ZOO](https://en.wikipedia.org/wiki/Zoo_(file_format)) | `Zoo` | `.zoo` | R/W | ✅ | defrag · wipe |  | [Archive Team](http://fileformats.archiveteam.org/wiki/ZOO) |
| [ZPAQ](https://en.wikipedia.org/wiki/ZPAQ) | `Zpaq` | `.zpaq` | R/W | ✅ | defrag | Reader covers the stored/simple models; ZPAQL virtual-machine execution is not implemented | [mattmahoney.net](http://mattmahoney.net/dc/zpaq.html) |

### 🧬 Structured data and registry

| Format | Id | Extensions | State | Test | Maintenance | Notes | Reference |
| --- | --- | --- | :---: | :---: | --- | --- | --- |
| JSON | `Json` | `.json` | WORM | ✅ | — | RFC 8259 object/array projection; CWB-created binary leaves use a versioned base64 envelope. Writer pinned byte-for-byte against CPython `json.dumps(obj, indent=2)`; reader against a document CPython wrote | [RFC 8259](https://www.rfc-editor.org/rfc/rfc8259) |
| XML | `Xml` | `.xml` | WORM | ✅ | — | XML 1.0 projection with DTD/external-entity resolution disabled; CWB creation uses a namespaced archive envelope. Reader pinned against a document Python's ElementTree wrote; the writer has no byte-parity gate, because no two XML writers agree on declaration quoting and namespace placement, and is instead checked live against a real parser in the advisory tier | [W3C XML 1.0](https://www.w3.org/TR/xml/) |
| MessagePack | `MessagePack` | `.msgpack` `.mpk` | WORM | ✅ | — | Maps, arrays, scalars and extension payloads; managed reader/writer. Writer pinned byte-for-byte against python-msgpack `packb`; reader against a vector covering nil/bool, the whole integer width ladder, float32 and float64, the str-vs-bin split, nesting, and ext types including timestamp | [MessagePack specification](https://github.com/msgpack/msgpack/blob/master/spec.md) |
| Python pickle | `Pickle` | `.pkl` `.pickle` | WORM | ✅ | — | Non-executing opcode VM; never imports modules or invokes `GLOBAL` / `REDUCE` / `BUILD` callables. Reads protocols 0-5, each pinned against output CPython wrote; writes protocol 4, pinned byte-for-byte against `pickle.dumps(obj, protocol=4)` | [Python](https://docs.python.org/3/library/pickle.html) |
| Perl Storable | `Storab…8643 tokens truncated…
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
| [ASF / WMV / WMA](https://en.wikipedia.org/wiki/Advanced_Systems_Format) | `Asf` | `.asf` `.wma` `.wmv` | ✅ | ✅ | ✅ | Header Object children and Data Object walked; unencrypted audio streams muxed and remuxed from the canonical stream entries | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/wmformat/overview-of-the-asf-format) |
| [AVI](https://en.wikipedia.org/wiki/Audio_Video_Interleave) | `Avi` | `.avi` | ✅ | ✅ | ✅ | movi demux; AVI 1.0 mux/remux preserves movi packet order and idx1 flags; elementary mux accepts video frames plus PCM audio; header chunks can be relocated in place | [Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/directshow/avi-riff-file-reference) |
| [Bink](https://en.wikipedia.org/wiki/Bink_Video) | `Bik` | `.bik` `.bk2` | ✅ | ✅ | — | Reverse-engineered; packet-aware demux preserves frame sizes, keyframes and interleaving for encoded-stream mux/remux; no codec encoder | [MultimediaWiki](https://wiki.multimedia.cx/index.php/Bink_Container) |
| [FLV](https://en.wikipedia.org/wiki/Flash_Video) | `Flv` | `.flv` | ✅ | ✅ | ✅ | AVC re-framed as Annex-B, AAC as ADTS, MP3 raw; other codecs as concatenated frames. Mux takes AAC/MP3 audio packets only; remux rewrites the container preserving native tag payloads, timestamps and order | [rtmp.veriskope.com](https://rtmp.veriskope.com/pdf/video_file_format_spec_v10_1.pdf) |
| [HLS M3U8](https://en.wikipedia.org/wiki/HTTP_Live_Streaming) | `M3u8` | `.m3u8` `.m3u` | ✅ | — | — | A manifest, not a container: it references segments it does not hold, so there is nothing to mux | [RFC](https://www.rfc-editor.org/rfc/rfc8216) |
| [Matroska / WebM](https://en.wikipedia.org/wiki/Matroska) | `Mkv` | `.mkv` `.webm` `.mka` `.mks` | ✅ | ✅ | ✅ | Tracks, attachments and chapters; Cues can be moved to the front in place | [matroska.org](https://www.matroska.org/technical/elements.html) |
| [MP4 / MOV / 3GP](https://en.wikipedia.org/wiki/MP4_file_format) | `Mp4` | `.mp4` `.m4v` `.m4a` `.mov` … | ✅ | ✅ | ✅ | Track demux; audio-only mux from AAC/PCM inputs; fast-start relayout in place | [ISO](https://www.iso.org/standard/83102.html) |
| [MPEG program stream / VOB](https://en.wikipedia.org/wiki/MPEG_program_stream) | `MpegPs` | `.mpg` `.mpeg` `.vob` `.m2p` … | ✅ | ✅ | ✅ | PES headers stripped; DVD private-stream-1 substreams (AC-3, DTS, LPCM, sub-picture) split. Mux rebuilds an MPEG-2 program stream from MPEG-1/2, MPEG-4 Part 2, AVC, HEVC, MPEG-audio and AAC elementary streams; DVD private streams stay read-only | [ISO](https://www.iso.org/standard/75928.html) |
| [MPEG transport stream](https://en.wikipedia.org/wiki/MPEG_transport_stream) | `MpegTs` | `.ts` `.m2ts` `.mts` | ✅ | ✅ | — | Per-PID elementary streams as raw PES; mux writes one PAT/PMT and packetises the supplied elementary streams | [ISO](https://www.iso.org/standard/75928.html) |
| [RealMedia](https://en.wikipedia.org/wiki/RealMedia) | `RealMedia` | `.rm` `.rmvb` `.ra` | ✅ | ✅ | — | Reverse-engineered; demux plus encoded-audio mux and remux, no video writer | [MultimediaWiki](https://wiki.multimedia.cx/index.php/RealMedia) |
| [Smacker](https://en.wikipedia.org/wiki/Smacker_video) | `Smk` | `.smk` | ✅ | — | — | Reverse-engineered; no writer | [MultimediaWiki](https://wiki.multimedia.cx/index.php/Smacker) |
| [Blu-ray PGS (.sup)](https://en.wikipedia.org/wiki/Presentation_Graphic_Stream) | `Sup` | `.sup` | ✅ | ✅ | ✅ | Complete PCS-to-END display sets can be reassembled losslessly; bitmap/timing authoring is outside this pseudo-archive surface | [GitHub](https://github.com/mjuhasz/BDSup2Sub) |
| [VobSub](https://en.wikipedia.org/wiki/VobSub) | `VobSub` | `.idx` | ✅ | ✅ | — | Index plus one sub-picture stream, not a multi-track container; raw `.spu` input muxes to 2 KiB MPEG-PS sectors and extracted `.bin` chunks are written back byte-exact | [sam.zoy.org](http://sam.zoy.org/writings/dvd/subtitles/) |

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

- **Media containers.** AVI, MPEG-TS, RealMedia and Smacker demux only; MP4, Matroska, ASF, Bink, FLV and MPEG-PS mux audio tracks only. This is a limit of what demuxing preserves, not of the container specs. The demuxers hand back each track as a codec elementary stream — AVC re-framed as Annex-B, AAC as ADTS, per-PID PES — and per-frame timestamps, interleaving order and the index live in the container layer that is dropped on the way out. Muxing those entries back would mean inventing presentation timing rather than restoring it, so the writers are not there. Closing this needs a demux surface that carries timed packets, not another writer. Bink, Smacker, RealMedia and ASF are additionally reverse-engineered rather than specified. M3U8, PGS `.sup` and VobSub are not container muxes at all: a playlist is a manifest referencing segments it does not contain, and the two subtitle formats are single streams. ISO-BMFF brands are parsed generically, without a brand registry.
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

The full model — tiers, archive vs. pseudo-archive, the five maintenance verbs and the composite `compact`, the block-map display contract and the streaming paths — is specified in [`docs/ARCHIVE-MODEL.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/ARCHIVE-MODEL.md). How the verbs are provided without bespoke per-format code, and the rule that decides when `CanModify` may be advertised, are in [`docs/MAINTENANCE-MECHANISMS.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/MAINTENANCE-MECHANISMS.md). Per-verb coverage of the filesystem descriptors is the support matrix of [`Hawkynt.FileFormats.FileSystems/README.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.FileSystems/README.md); for the archive descriptors it is the Maintenance column above.

### On-disk derivations

Two codecs this package writes have no published specification. What was measured to make them interoperate is written up so it is not lost: [`docs/LZMS-ON-DISK.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/LZMS-ON-DISK.md) (WIM LZMS resources, verified by `wimlib-imagex verify`) and [`docs/QUANTUM-ON-DISK.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/QUANTUM-ON-DISK.md) (CAB Quantum folders, verified by `cabextract`). The BitRock installer layout is documented beside its reader in [`Hawkynt.FileFormats.Archives/FileFormats/FileFormat.BitRock/FORMAT-NOTES.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.Archives/FileFormats/FileFormat.BitRock/FORMAT-NOTES.md). Size ceilings of the underlying building blocks are measured in [`docs/LARGE-INPUTS.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/LARGE-INPUTS.md).

## 🧭 When to use this package

Use it when a .NET process needs to enumerate, extract, test, create, edit or inspect a broad range of archives, packages, images and streams without native archive libraries. If all you need is ordinary ZIP at default settings, `System.IO.Compression.ZipArchive` is simpler. Original-vendor encoder parity for proprietary formats is not implied: readable, writable, interoperable output is the goal.

## 📚 API reference

<!-- API:BEGIN generated by Hawkynt/RepositoryTemplate/package-readme — edit the XML docs in source, not here -->

Every public and protected member of all 2552 types, generated from the built assembly and its XML documentation, is in [REFERENCE.md](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.Archives/REFERENCE.md).

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

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsors-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
