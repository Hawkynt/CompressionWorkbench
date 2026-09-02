# Hawkynt.FileFormats.Archives

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.FileFormats.Archives.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Archives/)
[![NuGet downloads](https://img.shields.io/nuget/dt/Hawkynt.FileFormats.Archives.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Archives/)
[![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
![Target](https://img.shields.io/badge/target-net10.0-blue)

> Pure-managed archive handling for .NET, on top of `Hawkynt.Compression.Core`. The package claims the
> WHOLE domain — every compression stream, archive container, software package, document bundle,
> installer payload and game archive — not a selection of it. Where a format is missing or only
> partly supported that is a tracked gap, recorded in the support matrix below and in
> [`docs/OPERATION_COVERAGE.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/OPERATION_COVERAGE.md).

## 📦 Installation

```bash
dotnet add package Hawkynt.FileFormats.Archives
```

The package bundles the archive-domain `FileFormat.*` assemblies while taking `Hawkynt.Compression.Core` as the shared NuGet dependency.

## ✨ Features

- Compression-stream readers/writers for modern and historical formats without native `zlib`, `liblzma`, `libarchive`, or `libbz2` runtime dependencies.
- Multi-file archive enumeration, extraction, testing, and fresh archive creation where supported.
- Software-package and installer inspection without executing the package/installer.
- ZIP-derived document/package families exposed through the same archive surface.
- Game, engine, Amiga, and vintage archive formats included alongside mainstream ZIP/TAR/7z/RAR families.
- Common `IArchiveFormatOperations` model for formats that carry independently addressable payloads.

## 🧩 Support matrix

| State | Meaning |
| --- | --- |
| **R** | List/extract/test only. |
| **WORM** | Read plus create a fresh archive/output; no in-place mutation. |
| **R/W** | Read plus supported modification semantics. |
| **⚠️** | Deliberate subset; see the row notes. |

### Compression streams

| Format | State | Notes | Reference |
| --- | :---: | --- | --- |
| [gzip](https://en.wikipedia.org/wiki/Gzip) | WORM | GZIP wrapper / DEFLATE payload | [RFC 1952](https://www.rfc-editor.org/rfc/rfc1952) |
| [zlib](https://en.wikipedia.org/wiki/Zlib) | WORM | zlib wrapper / DEFLATE payload | [RFC 1950](https://www.rfc-editor.org/rfc/rfc1950) |
| [bzip2](https://en.wikipedia.org/wiki/Bzip2) | WORM | BWT/MTF/Huffman stream | [bzip2 manual](https://sourceware.org/bzip2/manual/manual.html) |
| [XZ](https://en.wikipedia.org/wiki/XZ_Utils) | WORM | XZ container around LZMA2 and filters | [XZ file format](https://tukaani.org/xz/xz-file-format.txt) |
| [LZMA](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Markov_chain_algorithm) | WORM | Raw/LZMA stream handling | [7-Zip LZMA SDK](https://www.7-zip.org/sdk.html) |
| [Zstandard](https://en.wikipedia.org/wiki/Zstd) | WORM | Zstd frame stream | [RFC 8878](https://www.rfc-editor.org/rfc/rfc8878) |
| [Brotli](https://en.wikipedia.org/wiki/Brotli) | WORM | Brotli stream | [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932) |
| [LZ4 frame](https://en.wikipedia.org/wiki/LZ4_(compression_algorithm)) | WORM | LZ4 framed stream | [LZ4 frame format](https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md) |
| [Snappy](https://en.wikipedia.org/wiki/Snappy_(compression)) | WORM | Snappy stream/container surface | [Snappy format](https://github.com/google/snappy/blob/main/format_description.txt) |
| [Unix compress](https://en.wikipedia.org/wiki/Compress) | WORM | `.Z` / LZW | [POSIX compress](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/compress.html) |
| [Lzip](https://en.wikipedia.org/wiki/Lzip) | WORM | LZMA-based stream | [Lzip format](https://www.nongnu.org/lzip/manual/lzip_manual.html#File-format) |
| [Lzop](https://en.wikipedia.org/wiki/Lzop) | WORM | LZO-framed stream | [lzop](https://www.lzop.org/) |
| [LZFSE](https://en.wikipedia.org/wiki/LZFSE) | WORM ⚠️ | LZVN/uncompressed blocks; full LZFSE block families have limits noted below | [Apple LZFSE](https://github.com/lzfse/lzfse) |
| [PAQ](https://en.wikipedia.org/wiki/PAQ) | WORM | PAQ8-family stream surface | [Matt Mahoney PAQ](https://mattmahoney.net/dc/paq.html) |
| [PPMd](https://en.wikipedia.org/wiki/Prediction_by_partial_matching#PPMd) | WORM | PPMd stream surface | [7-Zip SDK](https://www.7-zip.org/sdk.html) |

### Archive containers

| Format | State | Notes | Reference |
| --- | :---: | --- | --- |
| [ZIP](https://en.wikipedia.org/wiki/ZIP_(file_format)) | WORM | Multi-file archive creation/extraction | [PKWARE APPNOTE](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT) |
| [TAR](https://en.wikipedia.org/wiki/Tar_(computing)) | WORM | POSIX/GNU/PAX tape archive | [POSIX pax](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html) |
| [7z](https://en.wikipedia.org/wiki/7z) | WORM | 7-Zip container | [7z format](https://www.7-zip.org/7z.html) |
| [RAR](https://en.wikipedia.org/wiki/RAR_(file_format)) | WORM ⚠️ | Clean-room creation; not a claim of WinRAR encoder parity | [RAR technote](https://www.rarlab.com/technote.htm) |
| [CAB](https://en.wikipedia.org/wiki/Cabinet_(file_format)) | WORM | Microsoft Cabinet | [MS-CAB](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cab/) |
| [WIM](https://en.wikipedia.org/wiki/Windows_Imaging_Format) | WORM | Windows Imaging Format | [Microsoft WIM](https://learn.microsoft.com/windows-hardware/manufacture/desktop/) |
| [CPIO](https://en.wikipedia.org/wiki/Cpio) | WORM | CPIO archive variants | [cpio(5)](https://www.freebsd.org/cgi/man.cgi?query=cpio&sektion=5) |
| [ar](https://en.wikipedia.org/wiki/Ar_(Unix)) | WORM | Unix archive/library container | [ar(5)](https://www.freebsd.org/cgi/man.cgi?query=ar&sektion=5) |
| [XAR](https://en.wikipedia.org/wiki/Xar_(archiver)) | WORM | Extensible Archive | [XAR on-disk format](https://github.com/mackyle/xar/wiki/xarformat) |
| [LHA/LZH](https://en.wikipedia.org/wiki/LHA_(file_format)) | WORM | Historical LZH archive family | [LHA archive format](http://www.math.sci.hiroshima-u.ac.jp/m-mat/MT/hamamura-home/lha-en.html) |
| [ARJ](https://en.wikipedia.org/wiki/ARJ) | WORM | ARJ archive | [ARJ Software](http://www.arjsoftware.com/) |
| [StuffIt](https://en.wikipedia.org/wiki/StuffIt) | WORM | Classic Macintosh archive family | [XADMaster](https://github.com/MacPaw/XADMaster) |
| [ZPAQ](https://en.wikipedia.org/wiki/ZPAQ) | WORM ⚠️ | Context-mixing/journaling family; reader VM limitation documented below | [ZPAQ specification](https://mattmahoney.net/dc/zpaq206.pdf) |

### Software / document package formats

| Format family | State | Notes | Reference |
| --- | :---: | --- | --- |
| [APK](https://en.wikipedia.org/wiki/Apk_(file_format)) | WORM | Android package container | [Android APK](https://source.android.com/docs/core/runtime/jit-compiler) |
| [Debian `.deb`](https://en.wikipedia.org/wiki/Deb_(file_format)) | WORM | `ar` + TAR payload | [deb(5)](https://man7.org/linux/man-pages/man5/deb.5.html) |
| [RPM](https://en.wikipedia.org/wiki/RPM_Package_Manager) | WORM | RPM package container | [RPM format](https://rpm-software-management.github.io/rpm/manual/format.html) |
| [NuGet `.nupkg`](https://en.wikipedia.org/wiki/NuGet) | WORM | ZIP-based NuGet package | [NuGet nuspec](https://learn.microsoft.com/nuget/reference/nuspec) |
| [APPX / MSIX](https://en.wikipedia.org/wiki/Appx) | WORM | Microsoft application packages | [MS APPX/MSIX](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/) |
| [JAR](https://en.wikipedia.org/wiki/JAR_(file_format)) | WORM | Java archive | [JAR specification](https://docs.oracle.com/en/java/javase/21/docs/specs/jar/jar.html) |
| [EPUB](https://en.wikipedia.org/wiki/EPUB) | WORM | ZIP-based publication bundle | [W3C EPUB 3](https://www.w3.org/TR/epub-33/) |
| [Office Open XML](https://en.wikipedia.org/wiki/Office_Open_XML) | WORM | DOCX/XLSX/PPTX package families | [ECMA-376](https://www.ecma-international.org/publications-and-standards/standards/ecma-376/) |
| [OpenDocument](https://en.wikipedia.org/wiki/OpenDocument) | WORM | ODT/ODS/ODP package families | [OASIS ODF](https://www.oasis-open.org/standard/odf/) |
| [CRX](https://en.wikipedia.org/wiki/Google_Chrome_Extension) | WORM ⚠️ | CRX3 envelope creation is unsigned | [Chrome extension hosting](https://developer.chrome.com/docs/extensions/mv3/linux_hosting/) |
| [XPI](https://en.wikipedia.org/wiki/XPInstall) | WORM | Mozilla extension package | [Mozilla XPI](https://developer.mozilla.org/en-US/docs/Mozilla/Tech/XPI) |

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

## 🧭 When to use this package

Use it when a .NET process needs to enumerate, extract, test, create or inspect a broad range of archives and compression streams without loading native archive libraries. Typical use cases include installer/package inspection, ZIP-derived documents, historical compression streams, game/engine assets, backup images, web bundles and scientific containers.

If all you need is ordinary ZIP at default BCL settings, `System.IO.Compression.ZipArchive` is usually simpler. Original-vendor encoder parity for proprietary formats is a different goal from clean-room readable/writable interoperability and is not implied here.

## 📚 Exhaustive compression-stream inventory

| Format project | State | Display name |
| --- | --- | --- |
| `FileFormat.ApLib` | WORM | aPLib |
| `FileFormat.Balz` | WORM | BALZ |
| `FileFormat.Bcm` | WORM | BCM |
| `FileFormat.BinHex` | WORM | BinHex |
| `FileFormat.BriefLz` | WORM | BriefLZ |
| `FileFormat.Brotli` | WORM | Brotli |
| `FileFormat.Bsc` | WORM | BSC |
| `FileFormat.Bzip2` | WORM | BZip2 |
| `FileFormat.Cmix` | WORM | cmix |
| `FileFormat.Compress` | WORM | Unix Compress |
| `FileFormat.Crunch` | WORM | CP/M Crunch |
| `FileFormat.Csc` | WORM | CSC |
| `FileFormat.Density` | WORM | Density |
| `FileFormat.Freeze` | WORM | Freeze |
| `FileFormat.Gzip` | WORM | GZIP |
| `FileFormat.IcePacker` | WORM | ICE Packer |
| `FileFormat.Kwaj` | WORM | KWAJ |
| `FileFormat.Lizard` | WORM | Lizard (LZ5) |
| `FileFormat.Lrzip` | WORM | Long Range Zip |
| `FileFormat.Lz4` | WORM | LZ4 |
| `FileFormat.Lzfse` | WORM ⚠️ | LZFSE |
| `FileFormat.Lzg` | WORM | LZG |
| `FileFormat.Lzham` | WORM | LZHAM |
| `FileFormat.Lzip` | WORM | Lzip |
| `FileFormat.Lzma` | WORM | LZMA |
| `FileFormat.Lzop` | WORM | LZOP |
| `FileFormat.Lzs` | WORM | LZS |
| `FileFormat.Lzx` | WORM | LZX |
| `FileFormat.MacBinary` | WORM | MacBinary |
| `FileFormat.Mcm` | WORM | MCM |
| `FileFormat.PackBits` | WORM | PackBits |
| `FileFormat.Paq8` | WORM | PAQ8 |
| `FileFormat.PowerPacker` | WORM | PowerPacker |
| `FileFormat.Ppmd` | WORM | PPMd |
| `FileFormat.QuickLz` | WORM | QuickLZ |
| `FileFormat.RefPack` | WORM | RefPack / QFS |
| `FileFormat.Rnc` | WORM | RNC ProPack |
| `FileFormat.Rzip` | WORM | Rzip |
| `FileFormat.Snappy` | WORM | Snappy |
| `FileFormat.Squeeze` | WORM | Squeeze |
| `FileFormat.Szdd` | WORM | SZDD |
| `FileFormat.UuEncoding` | WORM | UUEncoding |
| `FileFormat.Xz` | WORM | XZ |
| `FileFormat.YEnc` | WORM | yEnc |
| `FileFormat.Yaz0` | WORM | Yaz0 |
| `FileFormat.Zlib` | WORM | Zlib |
| `FileFormat.Zling` | WORM | Zling |
| `FileFormat.Zstd` | WORM | Zstandard |

## 📚 Exhaustive archive-container inventory

| Format project | State | Display name |
| --- | --- | --- |
| `FileFormat.Ace` | WORM | ACE |
| `FileFormat.AlZip` | WORM | ALZip |
| `FileFormat.AppleSingle` | R | AppleSingle |
| `FileFormat.Ar` | WORM | AR |
| `FileFormat.Arc` | WORM | ARC |
| `FileFormat.Arj` | WORM | ARJ |
| `FileFormat.Cab` | WORM | CAB |
| `FileFormat.Cbr` | WORM | CBR |
| `FileFormat.Cbz` | WORM | CBZ |
| `FileFormat.Chm` | WORM | CHM |
| `FileFormat.CompactPro` | WORM | Compact Pro |
| `FileFormat.Cpio` | WORM | CPIO |
| `FileFormat.DiskDoubler` | WORM | DiskDoubler |
| `FileFormat.Dms` | WORM | DMS |
| `FileFormat.Esd` | R | ESD |
| `FileFormat.FreeArc` | WORM | FreeArc |
| `FileFormat.Ha` | WORM | HA |
| `FileFormat.IffCdaf` | WORM | IFF CDAF |
| `FileFormat.Lbr` | WORM | LBR |
| `FileFormat.LhF` | WORM | LhF (LhFloppy) |
| `FileFormat.Lzh` | WORM | LZH |
| `FileFormat.PackDisk` | WORM | PackDisk (Amiga) |
| `FileFormat.PackIt` | WORM | PackIt |
| `FileFormat.Rar` | WORM | RAR |
| `FileFormat.Sar` | WORM | SAR |
| `FileFormat.SevenZip` | WORM | 7z |
| `FileFormat.Shar` | WORM | SHAR |
| `FileFormat.Spark` | WORM | Spark |
| `FileFormat.SplitFile` | WORM | Split File (.001) |
| `FileFormat.Sqx` | WORM | SQX |
| `FileFormat.StuffIt` | WORM | StuffIt |
| `FileFormat.StuffItX` | WORM ⚠️ | StuffIt X |
| `FileFormat.Swm` | R | Split WIM |
| `FileFormat.Tar` | WORM | TAR |
| `FileFormat.Uharc` | WORM | UHARC |
| `FileFormat.Wim` | WORM | WIM |
| `FileFormat.Wrapster` | WORM | Wrapster |
| `FileFormat.Xar` | WORM | XAR |
| `FileFormat.Zip` | WORM | ZIP |
| `FileFormat.Zoo` | WORM | ZOO |
| `FileFormat.Zpaq` | WORM ⚠️ | ZPAQ |

## 📦 Software-package containers

| Format | State | Display name |
| --- | --- | --- |
| `FileFormat.AndroidBundle` | R | Android App Bundle / split-APK |
| `FileFormat.AndroidOta` | R | Android OTA payload |
| `FileFormat.Apk` | WORM | APK |
| `FileFormat.ApkNativeLibs` | R | APK native libraries |
| `FileFormat.AppImage` | WORM | AppImage |
| `FileFormat.Appx` | WORM | APPX |
| `FileFormat.BitRock` | R | BitRock / InstallBuilder |
| `FileFormat.Crate` | WORM | Rust Crate |
| `FileFormat.Crx` | WORM ⚠️ | CRX / Chrome extension |
| `FileFormat.Deb` | WORM | DEB |
| `FileFormat.Ear` | WORM | EAR |
| `FileFormat.Gem` | R | Ruby Gem |
| `FileFormat.InnoSetup` | WORM | Inno Setup |
| `FileFormat.Ipa` | WORM | IPA |
| `FileFormat.Jar` | WORM | JAR |
| `FileFormat.Msi` | WORM ⚠️ | MSI / OLE CFB envelope |
| `FileFormat.Msix` | WORM | MSIX |
| `FileFormat.Nsis` | WORM ⚠️ | NSIS |
| `FileFormat.NuPkg` | WORM | NuPkg |
| `FileFormat.PyInstaller` | R | PyInstaller onefile |
| `FileFormat.Rpm` | WORM | RPM |
| `FileFormat.Snap` | R | Snap package |
| `FileFormat.War` | WORM | WAR |
| `FileFormat.Wheel` | R | Python Wheel |
| `FileFormat.Xpi` | WORM | XPI / Mozilla extension |

## 📄 Office, document and web bundles

| Format | State | Display name |
| --- | --- | --- |
| `FileFormat.Docx` | WORM | DOCX |
| `FileFormat.Xlsx` | WORM | XLSX |
| `FileFormat.Pptx` | WORM | PPTX |
| `FileFormat.Odt` | WORM | ODT |
| `FileFormat.Ods` | WORM | ODS |
| `FileFormat.Odp` | WORM | ODP |
| `FileFormat.Vsdx` | WORM | Visio Drawing |
| `FileFormat.Epub` | WORM | EPUB |
| `FileFormat.Kmz` | WORM | KMZ |
| `FileFormat.Maff` | WORM | MAFF |
| `FileFormat.Wacz` | R | WACZ |
| `FileFormat.Warc` | WORM | WARC |
| `FileFormat.Wbn` | R | Web Bundle |

## 🎮 Game, engine, Amiga and vintage archives

| Format | State | Display name |
| --- | --- | --- |
| `FileFormat.Afs` | WORM | Sega AFS |
| `FileFormat.Ampk` | WORM | AMPK / Amiga Pack |
| `FileFormat.Ba2` | WORM | Bethesda Archive v2 |
| `FileFormat.Big` | WORM | BIG / Westwood-EA |
| `FileFormat.Bsa` | WORM | BSA |
| `FileFormat.Dzip` | WORM | Bloodlines DZIP |
| `FileFormat.Gar` | WORM | Nintendo 3DS GAR |
| `FileFormat.Gob` | WORM | LucasArts GOB |
| `FileFormat.GodotPck` | WORM | Godot PCK |
| `FileFormat.Grp` | WORM | GRP / Build engine |
| `FileFormat.Hog` | WORM | HOG / Descent |
| `FileFormat.Hpi` | WORM ⚠️ | Total Annihilation HPI |
| `FileFormat.Lfd` | WORM | LucasArts LFD |
| `FileFormat.Mhk` | WORM | Cyan Mohawk |
| `FileFormat.Mix` | WORM | Westwood MIX |
| `FileFormat.Mpq` | WORM | Blizzard MPQ |
| `FileFormat.Narc` | WORM | Nintendo NARC |
| `FileFormat.Nds` | WORM ⚠️ | Nintendo DS ROM / NitroFS-oriented creation |
| `FileFormat.Nsa` | WORM | NScripter NSA |
| `FileFormat.Pak` | WORM | Quake PAK |
| `FileFormat.Pbp` | WORM | PSP PBP |
| `FileFormat.Psarc` | WORM ⚠️ | Sony PSARC |
| `FileFormat.Rgss` | WORM | RPG Maker RGSSAD |
| `FileFormat.Rpa` | WORM | Ren'Py Archive |
| `FileFormat.Sarc` | WORM | Nintendo SARC |
| `FileFormat.Sfar` | R | BioWare SFAR |
| `FileFormat.Slf` | WORM | Sir-Tech SLF |
| `FileFormat.Swf` | WORM | SWF |
| `FileFormat.Tfc` | WORM | Mass Effect TFC |
| `FileFormat.Tnef` | WORM | MS-TNEF / winmail.dat |
| `FileFormat.U8` | WORM | Nintendo U8 |
| `FileFormat.Umx` | WORM ⚠️ | Unreal Music package shell |
| `FileFormat.UnityBundle` | R | Unity Asset Bundle |
| `FileFormat.UnrealPak` | R | Unreal Pak |
| `FileFormat.Upx` | R | UPX-packed executable |
| `FileFormat.Vpk` | WORM | Source/Steam VPK |
| `FileFormat.Vpp` | WORM | Volition Package v1 |
| `FileFormat.VppV2` | WORM | Volition VPP v2 |
| `FileFormat.Wad` | WORM | Doom WAD |
| `FileFormat.Wad2` | WORM | Quake/Half-Life WAD2/WAD3 |
| `FileFormat.Ypf` | WORM | YukaScript YPF |
| `FileFormat.Zap` | WORM | Amiga ZAP |

## 💾 Backup-software disk images

| Format | State | Scope / evidence |
| --- | --- | --- |
| `FileFormat.Acronis` | R | Classic `.tib`; FileMeta chain / InputItem attribute stream fields decoded from reverse-engineered evidence |
| `FileFormat.AcronisTibx` | R | Modern `.tibx`; page-frame walk + LSM sub-header; record-stream decode remains bounded as documented in source |
| `FileFormat.Aomei` | WORM ⚠️ | `.adi` / `.afi`; BIFH/BIFT + BR standard-header/index structures; own writer round-trip is not claimed as vendor byte-compat |
| `FileFormat.AppleSparse` | R | Apple sparseimage / sparsebundle |
| `FileFormat.Bkf` | R | Microsoft NTBackup MTF |
| `FileFormat.EaseUs` | R | EaseUS Todo Backup `.pbd`; chunk-stream extraction path |
| `FileFormat.Ghost` | R/W | Norton Ghost modern record-stream path with in-place append/tombstone modifier plus legacy reader coverage |
| `FileFormat.Macrium` | WORM ⚠️ | Reflect X open-spec path + older Reflect read path; rebuild helper is distinct from in-place mutation |
| `FileFormat.Paragon` | WORM ⚠️ | Paragon `.pbf`; own clean-room container path, vendor byte-compat not implied |
| `FileFormat.Partclone` | R | Clonezilla/partclone images |
| `FileFormat.Veeam` | R ⚠️ | Veeam `.vbk`/`.vib`/`.vrb`; supported summary/trailer path rather than undocumented full block layer |

## 🔬 Detailed archive-container reference

| Format | Extensions | Read | Write | Reference | Notes |
| --- | --- | --- | --- | --- | --- |
| [ZIP](https://en.wikipedia.org/wiki/ZIP_(file_format)) | `.zip` | Yes | Yes | [APPNOTE.TXT](https://pkwaredownloads.blob.core.windows.net/pem/APPNOTE.txt) | Store, Deflate, Deflate64, Shrink, Reduce, Implode, BZip2, LZMA, PPMd, Zstd, AES |
| [RAR](https://en.wikipedia.org/wiki/RAR_(file_format)) | `.rar` | Yes | Yes (v4/v5) | [rarlab technote](https://www.rarlab.com/technote.htm) | v1-v5 readers; creation scope is v4/v5 and is not original-tool parity |
| [7z](https://en.wikipedia.org/wiki/7z) | `.7z` | Yes | Yes | [7-Zip format](https://www.7-zip.org/7z.html) | LZMA/LZMA2, Deflate, BZip2, PPMd, BCJ/BCJ2, AES-256, multi-volume paths |
| [TAR](https://en.wikipedia.org/wiki/Tar_(computing)) | `.tar` | Yes | Yes | [POSIX ustar](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html) | POSIX/GNU/PAX, multi-volume paths |
| [CAB](https://en.wikipedia.org/wiki/Cabinet_(file_format)) | `.cab` | Yes | Yes | [MS-CAB](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cab/) | MSZIP, LZX, Quantum |
| [LZH/LHA](https://en.wikipedia.org/wiki/LHA_(file_format)) | `.lzh`, `.lha` | Yes | Yes | [LHA archive format](http://www.math.sci.hiroshima-u.ac.jp/m-mat/MT/hamamura-home/lha-en.html) | lh0-lh7, lzs, lh1-lh3, pm0-pm2 paths |
| [ARJ](https://en.wikipedia.org/wiki/ARJ) | `.arj` | Yes | Yes | [ARJ technical](http://www.arjsoftware.com/) | Methods 0-4, garble encryption path |
| [ARC](https://en.wikipedia.org/wiki/ARC_(file_format)) | `.arc` | Yes | Yes | [ARC format](http://fileformats.archiveteam.org/wiki/ARC_(compression_format)) | Methods 0-9 |
| [ZOO](https://en.wikipedia.org/wiki/Zoo_(file_format)) | `.zoo` | Yes | Yes | [ZOO](http://fileformats.archiveteam.org/wiki/ZOO) | LZW/LZH paths |
| [ACE](https://en.wikipedia.org/wiki/ACE_(compressed_file_format)) | `.ace` | Yes | Yes | [acefile](https://github.com/droe/acefile) | ACE 1/2, solid/filter/encryption/recovery paths as implemented |
| SQX | `.sqx` | Yes | Yes | [encode.su SQX discussion](https://encode.su/threads/1290-SQX-(by-SpeedProject)) | LZH/multimedia/audio/solid/AES/recovery paths |
| [CPIO](https://en.wikipedia.org/wiki/Cpio) | `.cpio` | Yes | Yes | [cpio(5)](https://www.freebsd.org/cgi/man.cgi?query=cpio&sektion=5) | Binary, odc, newc, CRC |
| [AR](https://en.wikipedia.org/wiki/Ar_(Unix)) | `.ar` | Yes | Yes | [ar(5)](https://www.freebsd.org/cgi/man.cgi?query=ar&sektion=5) | Unix archive |
| [WIM](https://en.wikipedia.org/wiki/Windows_Imaging_Format) | `.wim` | Yes | Yes | [Microsoft WIM](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/) | LZX/XPRESS paths |
| [RPM](https://en.wikipedia.org/wiki/RPM_Package_Manager) | `.rpm` | Yes | Yes | [RPM spec](https://rpm-software-management.github.io/rpm/manual/format.html) | CPIO payload |
| [DEB](https://en.wikipedia.org/wiki/Deb_(file_format)) | `.deb` | Yes | Yes | [deb(5)](https://man7.org/linux/man-pages/man5/deb.5.html) | AR + TAR with gz/xz/zst/bz2 |
| [Shar](https://en.wikipedia.org/wiki/Shar) | `.shar` | Yes | Yes | [GNU sharutils](https://www.gnu.org/software/sharutils/) | Shell archive |
| PAK | `.pak` | Yes | Yes | [PAK](http://fileformats.archiveteam.org/wiki/PAK) | ARC-compatible family where applicable |
| [HA](https://en.wikipedia.org/wiki/HA_(file_format)) | `.ha` | Yes | Yes | [HA](http://fileformats.archiveteam.org/wiki/HA) | HSC/ASC arithmetic coding |
| [ZPAQ](https://en.wikipedia.org/wiki/ZPAQ) | `.zpaq` | Yes ⚠️ | Yes | [ZPAQ spec](https://mattmahoney.net/dc/zpaq206.pdf) | Writer surface exists; full reader requires ZPAQL VM support noted in limitations |
| [StuffIt](https://en.wikipedia.org/wiki/StuffIt) | `.sit` | Yes | Yes | [XADMaster](https://github.com/MacPaw/XADMaster) | Multiple historical methods |
| StuffIt X | `.sitx` | Yes | Yes ⚠️ | [XADMaster](https://github.com/MacPaw/XADMaster) | Writer emits the supported envelope shell; full proprietary element catalog is not claimed |
| [NSIS](https://en.wikipedia.org/wiki/Nullsoft_Scriptable_Install_System) | `.exe` | Yes | Yes ⚠️ | [NSIS docs](https://nsis.sourceforge.io/Docs/) | Extraction + overlay-oriented WORM output, not a PE installer builder |
| Inno Setup | `.exe` | Yes | Yes ⚠️ | [innounp](https://sourceforge.net/projects/innounp/) | Extraction + supported signature/container output, not an installer compiler |
| [DMS](https://en.wikipedia.org/wiki/Disk_Masher_System) | `.dms` | Yes | Yes | [xDMS](https://github.com/markrabjohn/xDMS) | Amiga disk archiver |
| [LZX (Amiga)](https://en.wikipedia.org/wiki/LZX) | `.lzx` | Yes | Yes | [Amiga LZX](http://fileformats.archiveteam.org/wiki/LZX) | Amiga LZX |
| [Compact Pro](https://en.wikipedia.org/wiki/Compact_Pro) | `.cpt` | Yes | Yes | [XADMaster](https://github.com/MacPaw/XADMaster) | Classic Mac |
| Spark | `.spark` | Yes | Yes | [Spark](http://fileformats.archiveteam.org/wiki/Spark) | RISC OS |
| [LBR](https://en.wikipedia.org/wiki/LU_(software)) | `.lbr` | Yes | Yes | [CP/M LBR](http://www.gaby.de/cpm/manuals/archive/lbr.txt) | CP/M |
| UHARC | `.uha` | Yes | Yes | [UHARC](http://www.uharc.com/) | LZP-oriented path |
| [WAD](https://en.wikipedia.org/wiki/Doom_WAD) | `.wad` | Yes | Yes | [Doom Wiki](https://doomwiki.org/wiki/WAD) | Doom |
| WAD2/WAD3 | `.wad` | Yes | Yes | [Quake Wiki](https://quakewiki.org/wiki/.wad) | Quake/Half-Life textures |
| [XAR](https://en.wikipedia.org/wiki/Xar_(archiver)) | `.xar` | Yes | Yes | [XAR format](https://github.com/mackyle/xar/wiki/xarformat) | Apple package/container use |
| [ALZip](https://en.wikipedia.org/wiki/ALZip) | `.alz` | Yes | Yes | [ALZ](http://fileformats.archiveteam.org/wiki/ALZ) | Deflate-oriented path |
| VPK | `.vpk` | Yes | Yes | [Valve VPK](https://developer.valvesoftware.com/wiki/VPK_(file_format)) | Valve/Source |
| BSA | `.bsa` | Yes | Yes | [UESP BSA](https://en.uesp.net/wiki/Skyrim_Mod:File_Formats/BSA) | Bethesda generations supported by implementation |
| BA2 | `.ba2` | Yes | Yes | [UESP BA2](https://en.uesp.net/wiki/Skyrim_Mod:File_Formats/BA2) | BTDX/GNRL scope |
| [MPQ](https://en.wikipedia.org/wiki/MPQ) | `.mpq` | Yes | Yes | [StormLib](https://github.com/ladislav-zezula/StormLib) | Blizzard MPQ path |
| [GRP](https://moddingwiki.shikadi.net/wiki/GRP_(Build)_Format) | `.grp` | Yes | Yes | [Build GRP](https://moddingwiki.shikadi.net/wiki/GRP_(Build)_Format) | Build engine |
| [HOG](https://en.wikipedia.org/wiki/HOG_(file_format)) | `.hog` | Yes | Yes | [Descent HOG](http://descent.wikia.com/wiki/HOG) | Descent |
| BIG | `.big` | Yes | Yes | [EA BIG](http://wiki.xentax.com/index.php/EA_BIG) | EA/Westwood |
| Godot PCK | `.pck` | Yes | Yes | [Godot PCK](https://docs.godotengine.org/en/stable/development/file_formats/pck.html) | Godot |
| [WARC](https://en.wikipedia.org/wiki/Web_ARChive) | `.warc` | Yes | Yes | [ISO 28500](https://iipc.github.io/warc-specifications/) | WORM emits resource records for supplied files |
| NDS | `.nds` | Yes | Yes ⚠️ | [GBATEK](https://problemkaputt.de/gbatek.htm) | NitroFS-oriented output, not ARM boot code synthesis |
| NSA | `.nsa` | Yes | Yes | [NScripter](https://www.nscripter.com/) | Stored-entry writer path |
| SAR | `.sar` | Yes | Yes | [NScripter](https://www.nscripter.com/) | Uncompressed NSA family |
| PackIt | `.pit` | Yes | Yes | [XADMaster](https://github.com/MacPaw/XADMaster) | Classic Mac |
| DiskDoubler | `.dd` | Yes | Yes | [XADMaster](https://github.com/MacPaw/XADMaster) | Classic Mac; supported stored writer path |
| MSI | `.msi` | Yes | Yes ⚠️ | [MS-CFB](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/) | CFB envelope; not a synthesized Windows Installer DB |
| [PDF](https://en.wikipedia.org/wiki/PDF) | `.pdf` | Yes | Yes ⚠️ | [ISO 32000](https://www.iso.org/standard/75839.html) | Image extraction + file-attachment WORM surface, not a general PDF renderer/editor |
| [TNEF](https://en.wikipedia.org/wiki/Transport_Neutral_Encapsulation_Format) | `.tnef`, `.dat` | Yes | Yes | [MS-OXTNEF](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxtnef/) | Outlook `winmail.dat` |
| Split File | `.001` | Yes | Yes | — | Multi-part joining/splitting |
| FreeArc | `.arc` | Yes | Yes | [FreeArc](https://github.com/Bulat-Ziganshin/FreeArc) | FreeArc |
| [CHM](https://en.wikipedia.org/wiki/Microsoft_Compiled_HTML_Help) | `.chm` | Yes | Yes | [CHM spec archive](https://archive.org/details/chmspec) | Supported section-0/LZX creation paths |
| Wrapster | — | Yes | Yes | [XADMaster](https://github.com/MacPaw/XADMaster) | MP3 wrapper archive |
| LhF | `.lhf` | Yes | Yes | [XADMaster](https://github.com/MacPaw/XADMaster) | Amiga LhFloppy |
| ZAP | `.zap` | Yes | Yes | [XADMaster](https://github.com/MacPaw/XADMaster) | Amiga disk archiver |
| PackDisk | `.pdsk` | Yes | Yes | [XADMaster](https://github.com/MacPaw/XADMaster) | Amiga PackDisk family |
| AMPK | — | Yes | Yes | [XADMaster](https://github.com/MacPaw/XADMaster) | Amiga AMPK |
| IFF-CDAF | — | Yes | Yes | [IFF](http://fileformats.archiveteam.org/wiki/IFF) | IFF-CDAF archive |
| UMX | `.umx` | Yes | Yes ⚠️ | [Beyond Unreal package format](https://wiki.beyondunreal.com/Legacy:Package_File_Format) | Header/package-shell output only; full export-table music encoding not claimed |
| PSARC | `.psarc` | Yes | Yes ⚠️ | [PSARC](https://www.psdevwiki.com/ps3/PlayStation_archive_(PSARC)) | zlib block path; unsupported encrypted/LZMA variants rejected |
| MIX | `.mix` | Yes | Yes | [OpenRA MixFile](https://github.com/OpenRA/OpenRA/blob/bleed/OpenRA.Mods.Cnc/FileSystem/MixFile.cs) | Hash-keyed names; reader synthesizes hex names where original names are absent |
| VPP | `.vpp` | Yes | Yes | [Red Faction VPP](http://www.redfactionwiki.com/wiki/RF1:VPP_File_Format) | Volition v1 |
| PBP | `.pbp` | Yes | Yes | [PSP PBP](https://www.psdevwiki.com/psp/PBP) | Fixed EBOOT sections |
| GOB | `.gob`, `.goo` | Yes | Yes | [GOB overview](https://www.moddb.com/games/star-wars-jedi-knight-jedi-academy/tutorials/gob-pak-format-explained) | LucasArts |
| LFD | `.lfd` | Yes | Yes | [LucasArts LFD archive reference](https://web.archive.org/web/20140805170029/http://www.lucasforums.com/showthread.php?t=131803) | Type/name resources + RMAP index |
| PFS0 | `.nsp`, `.pfs0` | Yes | Yes | [Switchbrew PFS0](https://switchbrew.org/wiki/NCA_Format#PFS0) | Nintendo Switch PartitionFS |
| SLF | `.slf` | Yes | Yes | [JA2-Stracciatella](https://github.com/ja2-stracciatella/ja2-stracciatella/blob/master/src/sgp/SlfReader.cc) | Sir-Tech library |
| HPI | `.hpi`, `.ufo`, `.ccx`, `.gp3` | Yes | Yes ⚠️ | [TA HPI](https://units.tauniverse.com/tutorials/tadesign/tutorials/hpi.htm) | Supported unencrypted/zlib-oriented subset |
| SARC | `.sarc`, `.pack`, `.bars` | Yes | Yes | [3DBrew SARC](https://www.3dbrew.org/wiki/SARC) | Endian-aware reader; hash-sorted writer |
| AFS | `.afs` | Yes | Yes | [AFS](http://wiki.xentax.com/index.php/SEGA_Athena_Filesystem_(AFS)) | Sega Athena FS, alignment/metadata paths |
| NARC | `.narc`, `.carc` | Yes | Yes | [GBATEK NARC](https://problemkaputt.de/gbatek.htm#dscartridgenitrosdkbinaries) | Nintendo DS archive |
| SFAR | `.sfar` | Yes | — | [ME3Tweaks SFAR](https://me3tweaks.com/me3tweaks-help-and-info/me3-modding-assistant/sfar-files) | BioWare Mass Effect 3 DLC; LZX payload limits documented in source |
| PSF | `.psf`, `.minipsf`, `.ssf`, `.dsf`, `.gsf`, `.usf`, `.2sf`, `.ncsf`, `.snsf`, `.qsf` | Yes | Yes | [Neil Corlett PSF](https://web.archive.org/web/20060212232218/http://wiki.neillcorlett.com/PSFFormat) | Chiptune pseudo-archive surface |
| MHK | `.mhk` | Yes | Yes | [ScummVM Mohawk](https://github.com/scummvm/scummvm/tree/master/engines/mohawk) | Cyan Mohawk |
| YPF | `.ypf` | Yes | Yes | [crass](https://github.com/regomne/crass) | YukaScript v480-oriented path |
| U8 | `.u8`, `.arc` | Yes | Yes | [Tockdom U8](https://wiki.tockdom.com/wiki/U8_(File_Format)) | Nintendo U8 |
| AKB | `.akb` | Yes | Yes | [vgmstream AKB](https://github.com/vgmstream/vgmstream/blob/master/src/meta/akb.c) | Raw audio bytes + metadata surface |
| AWB / AFS2 | `.awb`, `.acb` | Yes | Yes | [CRI Wave Bank](http://wiki.xentax.com/index.php/CRI_Wave_Bank) | Alignment/offset-width aware |
| Web Bundle | `.wbn` | Yes | — | [Bundled Exchanges draft](https://datatracker.ietf.org/doc/draft-yasskin-wpack-bundled-exchanges/) | Minimal CBOR/pseudo-archive path |
| LRZIP | `.lrz` | Yes | Yes ⚠️ | [lrzip](https://github.com/ckolivas/lrzip) | Supported LZMA wrapper subtype; other subtypes rejected |
| GAR | `.gar` | Yes | Yes | [3DBrew GAR](https://www.3dbrew.org/wiki/GAR) | Nintendo 3DS asset resource |
| ARSC | `.arsc` | Yes | — | [AOSP ResourceTypes](https://android.googlesource.com/platform/frameworks/base/+/master/libs/androidfw/include/androidfw/ResourceTypes.h) | Android resource-table pseudo-archive |

## 📦 ZIP-derived containers

All of these delegate to the ZIP reader/writer or a closely related archive implementation. WORM means a fresh container can be produced with the package-specific outer expectations; it does not imply signatures or application-specific semantics are synthesized.

| Format | Extensions | Read | Write | Reference | Notes |
| --- | --- | --- | --- | --- | --- |
| [JAR](https://en.wikipedia.org/wiki/JAR_(file_format)) | `.jar` | Yes | Yes | [JAR spec](https://docs.oracle.com/en/java/javase/21/docs/specs/jar/jar.html) | Java archive |
| WAR | `.war` | Yes | Yes | [Java EE WAR](https://docs.oracle.com/javaee/7/tutorial/packaging003.htm) | Java web archive |
| EAR | `.ear` | Yes | Yes | [Java EE EAR](https://docs.oracle.com/javaee/7/tutorial/packaging004.htm) | Java enterprise archive |
| [APK](https://en.wikipedia.org/wiki/Apk_(file_format)) | `.apk` | Yes | Yes | [Android APK](https://source.android.com/docs/core/runtime/jit-compiler) | Android package |
| [IPA](https://en.wikipedia.org/wiki/.ipa) | `.ipa` | Yes | Yes | [Apple documentation](https://developer.apple.com/documentation/) | iOS package |
| APPX | `.appx`, `.msix` | Yes | Yes | [MS-APPXPKG](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/) | Windows package |
| [XPI](https://en.wikipedia.org/wiki/XPInstall) | `.xpi` | Yes | Yes | [Mozilla XPI](https://developer.mozilla.org/en-US/docs/Mozilla/Tech/XPI) | Firefox extension |
| CRX | `.crx` | Yes | Yes ⚠️ | [Chrome CRX3](https://developer.chrome.com/docs/extensions/mv3/linux_hosting/) | Unsigned writer output is structurally useful but not browser-trusted |
| [EPUB](https://en.wikipedia.org/wiki/EPUB) | `.epub` | Yes | Yes | [EPUB 3](https://www.w3.org/TR/epub-33/) | eBook |
| MAFF | `.maff` | Yes | Yes | [MAFF](http://maf.mozdev.org/maff-specification.html) | Mozilla archive |
| [KMZ](https://en.wikipedia.org/wiki/Keyhole_Markup_Language) | `.kmz` | Yes | Yes | [OGC KML](https://www.ogc.org/standards/kml) | Google Earth |
| NuPkg | `.nupkg` | Yes | Yes | [NuGet nuspec](https://learn.microsoft.com/en-us/nuget/reference/nuspec) | NuGet package |
| [DOCX](https://en.wikipedia.org/wiki/Office_Open_XML) | `.docx` | Yes | Yes | [ECMA-376](https://www.ecma-international.org/publications-and-standards/standards/ecma-376/) | Word OOXML |
| XLSX | `.xlsx` | Yes | Yes | [ECMA-376](https://www.ecma-international.org/publications-and-standards/standards/ecma-376/) | Excel OOXML |
| PPTX | `.pptx` | Yes | Yes | [ECMA-376](https://www.ecma-international.org/publications-and-standards/standards/ecma-376/) | PowerPoint OOXML |
| [ODT](https://en.wikipedia.org/wiki/OpenDocument) | `.odt` | Yes | Yes | [OASIS ODF](https://www.oasis-open.org/standard/odf/) | OpenDocument Text |
| ODS | `.ods` | Yes | Yes | [OASIS ODF](https://www.oasis-open.org/standard/odf/) | Spreadsheet |
| ODP | `.odp` | Yes | Yes | [OASIS ODF](https://www.oasis-open.org/standard/odf/) | Presentation |
| CBZ | `.cbz` | Yes | Yes | [Comic book archive](https://en.wikipedia.org/wiki/Comic_book_archive) | ZIP comic book |
| CBR | `.cbr` | Yes | Yes | [Comic book archive](https://en.wikipedia.org/wiki/Comic_book_archive) | RAR-backed comic book |
| XPS / OXPS | `.xps`, `.oxps` | Yes | Yes | [ECMA-388](https://ecma-international.org/publications-and-standards/standards/ecma-388/) | OpenXPS |
| VSDX | `.vsdx`, `.vstx` | Yes | Yes | [Visio formats](https://learn.microsoft.com/en-us/office/client-developer/visio/visio-file-formats) | Visio modern formats |

## 🗄️ OLE2 Compound File variants

DOC/XLS/PPT/MSG/Thumbs.db/MSI are built on [Compound File Binary](https://en.wikipedia.org/wiki/Compound_File_Binary_Format). WORM creation produces a structurally valid CFB envelope but does **not** synthesize the application-specific internal database/document streams. Known CFB writer boundaries include a limited DIFAT footprint, a single root storage model, and stream-name limits; treat these as container-level writers rather than application document generators.

| Format | Extensions | Read | Write | Reference | Scope |
| --- | --- | --- | --- | --- | --- |
| DOC | `.doc` | Yes | Yes ⚠️ | [MS-DOC](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-doc/) | CFB envelope, not Word binary document generation |
| XLS | `.xls` | Yes | Yes ⚠️ | [MS-XLS](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-xls/) | CFB envelope, not workbook generation |
| PPT | `.ppt` | Yes | Yes ⚠️ | [MS-PPT](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-ppt/) | CFB envelope, not presentation generation |
| MSG | `.msg` | Yes | Yes ⚠️ | [MS-OXMSG](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxmsg/) | CFB envelope, not MAPI property synthesis |
| Thumbs.db | `Thumbs.db` | Yes | Yes ⚠️ | [ForensicsWiki](https://www.forensicswiki.xyz/wiki/Thumbs.db) | CFB envelope, not Windows Catalog synthesis |
| MSI | `.msi` | Yes | Yes ⚠️ | [MS-MSI](https://learn.microsoft.com/en-us/windows/win32/msi/windows-installer-file-format) | CFB envelope, not functional Installer DB synthesis |

## 🧰 Compression-stream format reference

| Format | Extensions | Compress | Decompress | Reference |
| --- | --- | --- | --- | --- |
| [Gzip](https://en.wikipedia.org/wiki/Gzip) | `.gz` | Yes | Yes | [RFC 1952](https://www.rfc-editor.org/rfc/rfc1952) |
| [BZip2](https://en.wikipedia.org/wiki/Bzip2) | `.bz2` | Yes | Yes | [bzip2](https://sourceware.org/bzip2/) |
| [XZ](https://en.wikipedia.org/wiki/XZ_Utils) | `.xz` | Yes | Yes | [XZ format](https://tukaani.org/xz/xz-file-format.txt) |
| [Zstandard](https://en.wikipedia.org/wiki/Zstd) | `.zst` | Yes | Yes | [RFC 8878](https://www.rfc-editor.org/rfc/rfc8878) |
| [LZ4](https://en.wikipedia.org/wiki/LZ4_(compression_algorithm)) | `.lz4` | Yes | Yes | [LZ4 frame](https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md) |
| [Brotli](https://en.wikipedia.org/wiki/Brotli) | `.br` | Yes | Yes | [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932) |
| [Snappy](https://en.wikipedia.org/wiki/Snappy_(compression)) | `.sz`, `.snappy` | Yes | Yes | [Snappy framing](https://github.com/google/snappy/blob/main/framing_format.txt) |
| [LZOP](https://en.wikipedia.org/wiki/Lzop) | `.lzo` | Yes | Yes | [lzop](https://www.lzop.org/) |
| [compress (.Z)](https://en.wikipedia.org/wiki/Compress_(software)) | `.Z` | Yes | Yes | [ncompress](https://github.com/vapier/ncompress) |
| [LZMA](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Markov_chain_algorithm) | `.lzma` | Yes | Yes | [LZMA SDK](https://www.7-zip.org/sdk.html) |
| [Lzip](https://en.wikipedia.org/wiki/Lzip) | `.lz` | Yes | Yes | [lzip](https://www.nongnu.org/lzip/manual/lzip_manual.html) |
| [Zlib](https://en.wikipedia.org/wiki/Zlib) | `.zlib` | Yes | Yes | [RFC 1950](https://www.rfc-editor.org/rfc/rfc1950) |
| SZDD | `.sz_` | Yes | Yes | [stdlib format notes](https://www.stdlib.at/) |
| KWAJ | — | Yes | Yes | [KWAJ](http://fileformats.archiveteam.org/wiki/KWAJ) |
| [RZIP](https://en.wikipedia.org/wiki/Rzip) | `.rz` | Yes | Yes | [rzip](http://rzip.samba.org/) |
| [MacBinary](https://en.wikipedia.org/wiki/MacBinary) | `.bin` | Yes | Yes | [RFC 1740](https://www.rfc-editor.org/rfc/rfc1740) |
| [BinHex](https://en.wikipedia.org/wiki/BinHex) | `.hqx` | Yes | Yes | [RFC 1741](https://www.rfc-editor.org/rfc/rfc1741) |
| Squeeze | `.sqz` | Yes | Yes | [SQ](http://fileformats.archiveteam.org/wiki/SQ) |
| PowerPacker | `.pp` | Yes | Yes | [PowerPacker](http://fileformats.archiveteam.org/wiki/Powerpacker) |
| ICE Packer | `.ice` | Yes | Yes | [ICE](http://fileformats.archiveteam.org/wiki/ICE) |
| [PackBits](https://en.wikipedia.org/wiki/PackBits) | `.packbits` | Yes | Yes | [Apple PackBits](https://en.wikipedia.org/wiki/PackBits) |
| Yaz0 | `.yaz0`, `.szs` | Yes | Yes | [YAZ0](https://wiki.tockdom.com/wiki/YAZ0) |
| BriefLZ | `.blz` | Yes | Yes | [brieflz](https://github.com/jibsen/brieflz) |
| RNC | `.rnc` | Yes | Yes | [Rob Northen](http://segaretro.org/Rob_Northen_compression) |
| RefPack/QFS | `.qfs`, `.refpack` | Yes | Yes | [RefPack](http://wiki.niotso.org/RefPack) |
| aPLib | `.aplib` | Yes | Yes | [aPLib](http://ibsensoftware.com/products_aPLib.html) |
| [LZFSE](https://en.wikipedia.org/wiki/LZFSE) | `.lzfse` | Yes ⚠️ | Yes ⚠️ | [Apple LZFSE](https://github.com/lzfse/lzfse) |
| Freeze | `.f`, `.freeze` | Yes | Yes | [Freeze](http://fileformats.archiveteam.org/wiki/Freeze) |
| [uuencoding](https://en.wikipedia.org/wiki/Uuencoding) | `.uu`, `.uue` | Yes | Yes | [POSIX uuencode](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/uuencode.html) |
| [yEnc](https://en.wikipedia.org/wiki/YEnc) | `.yenc` | Yes | Yes | [yEnc draft](http://www.yenc.org/yenc-draft.1.3.txt) |
| Density | `.density` | Yes | Yes | [density](https://github.com/k0dai/density) |
| LZG | `.lzg` | Yes | Yes | [liblzg](https://github.com/mbitsnbites/liblzg) |
| BCM | `.bcm` | Yes | Yes | [BCM](https://github.com/encode84/bcm) |
| BSC | `.bsc` | Yes | Yes | [libbsc](https://github.com/IlyaGrebnov/libbsc) |
| BALZ | `.balz` | Yes | Yes | [BALZ](https://sourceforge.net/projects/balz/) |
| CSC | `.csc` | Yes | Yes | [CSC](https://github.com/fusiyuan2010/CSC) |
| Zling | `.zling` | Yes | Yes | [libzling](https://github.com/richox/libzling) |
| Lizard | `.lizard` | Yes | Yes | [Lizard](https://github.com/inikep/lizard) |
| QuickLZ | `.quicklz` | Yes | Yes | [QuickLZ](http://www.quicklz.com/) |
| cmix | `.cmix` | Yes | Yes | [cmix](https://github.com/byronknoll/cmix) |
| MCM | `.mcm` | Yes | Yes | [mcm](https://github.com/mathieuchartier/mcm) |
| [PAQ8](https://en.wikipedia.org/wiki/PAQ) | `.paq8` | Yes | Yes | [Matt Mahoney](https://mattmahoney.net/dc/) |
| [SWF](https://en.wikipedia.org/wiki/SWF) | `.swf` | Yes | Yes | [SWF 19](https://open-flash.github.io/mirrors/swf-spec-19.pdf) |
| CP/M Crunch | `.cru` | Yes | Yes | [CP/M archive docs](http://www.retroarchive.org/docs/cpm.html) |
| [PPMd](https://en.wikipedia.org/wiki/Prediction_by_partial_matching) | `.pmd` | Yes | Yes | [PPMd](https://github.com/jk-jeon/PPMd) |
| LZHAM | `.lzham` | Yes | Yes | [LZHAM](https://github.com/richgel999/lzham_codec) |
| LZS | `.lzs` | Yes | Yes | [RFC 1967](https://www.rfc-editor.org/rfc/rfc1967) / [RFC 2395](https://www.rfc-editor.org/rfc/rfc2395) |

## 🔗 Compound formats

`tar.gz`, `tar.bz2`, `tar.xz`, `tar.zst`, `tar.lz4`, `tar.lz`, and `tar.br` are composed from the inner TAR archive and the matching outer stream format. Read detection and writing reuse those two layers instead of introducing a second independent TAR implementation.

## 🧩 Pseudo-archive containers

Some formats are not conventionally called archives but naturally contain independently addressable payloads. They expose `IArchiveFormatOperations.List` entries for those payloads.

| Container | State | Description |
| --- | --- | --- |
| `FileFormat.PeResources` | R | PE/COFF `.rsrc` resources |
| `FileFormat.ResourceDll` | WORM | Pure-resource DLL surface + fresh DLL creation |
| `FileFormat.ExePackers` | R | Packer/protector detection evidence as `metadata.ini` + payload where available |
| `FileFormat.Ico` | WORM | ICO/CUR entries as image payloads |
| `FileFormat.Ani` | R | Animated cursor frames |
| `FileFormat.FontCollection` | R | TTC/OTC member fonts and supported glyph surfaces |
| `FileFormat.Gettext` | R | gettext `.mo`/`.po` message entries |

## 🎞️ Streaming, video and subtitle containers

| Container | State | Description |
| --- | --- | --- |
| `FileFormat.Mp4` | R | MP4/MOV atom walker; tracks surfaced as elementary/carried payloads |
| `FileFormat.Matroska` | R | MKV/WebM EBML tracks, attachments and chapters |
| `FileFormat.Avi` | R | AVI RIFF `movi` demux surface |
| `FileFormat.MpegTs` | R | MPEG-2 transport stream per-PID elementary streams |
| `FileFormat.Sup` | R | Blu-ray PGS subtitle epochs/segments |
| `FileFormat.VobSub` | R | DVD VobSub entry slices |
| `FileFormat.M3u8` | R | HLS segments and variant metadata |

## 🧪 Scientific / ML data containers

| Container | State | Reference | Notes |
| --- | --- | --- | --- |
| `FileFormat.Numpy` | R | [NEP 1](https://numpy.org/neps/nep-0001-npy-format.html) | `.npy` and `.npz`; shape/dtype/fortran metadata |
| `FileFormat.Hdf4` | R | [HDF4](https://support.hdfgroup.org/release4/doc/) | DD linked-list walker |
| `FileFormat.Hdf5` | R | [HDF5 format](https://docs.hdfgroup.org/hdf5/v1_14/_f_m_t3.html) | Superblock/group pseudo-archive surface |
| `FileFormat.Nifti` | R | [NIfTI](https://nifti.nimh.nih.gov/nifti-1/documentation) | v1/v2 header + voxel data, gzip transparent |
| `FileFormat.Onnx` | R | [ONNX proto](https://github.com/onnx/onnx/blob/main/onnx/onnx.proto) | Managed protobuf reader; graph initializers surfaced |
| `FileFormat.Dicom` | R | [DICOM](https://www.dicomstandard.org/current) | Image + DICOMDIR study/series surface |

## 🧊 CAD / 3D scene formats

| Container | State | Reference | Notes |
| --- | --- | --- | --- |
| `FileFormat.Stl` | R | [STL](http://www.ennex.com/~fabbers/StL.asp) | ASCII/binary triangle data |
| `FileFormat.Ply` | R | [Stanford PLY](http://paulbourke.net/dataformats/ply/) | ASCII/binary LE/BE schema |
| `FileFormat.Dxf` | R | [Autodesk DXF](https://help.autodesk.com/view/OARX/2022/ENU/?guid=GUID-235B22E0-A567-4CF6-92D3-38A2306D73F3) | ASCII sections/entities |
| `FileFormat.Collada` | R | [Khronos COLLADA](https://www.khronos.org/files/collada_spec_1_5.pdf) | XML scene interchange |
| `FileFormat.Obj` | R | [Wavefront OBJ](https://en.wikipedia.org/wiki/Wavefront_.obj_file) | Mesh/material surface |

## 🛡️ Executable packer detection

`FileFormat.ExePackers` surfaces evidence about packers/protectors such as PKLITE, LZEXE, Petite, Shrinkler, FSG, MEW, MPRESS, Crinkler, kkrunchy, ASPack, NsPack, Yoda's Crypter, ASProtect, Themida and VMProtect. UPX has its own descriptor with signature/evidence-based detection and supported in-process payload decompression paths; PE-header reconstruction that requires vendor-tool semantics is not misrepresented as an internal capability.

## 🧪 Known limitations

| Area | State |
| --- | --- |
| LZFSE V1/V2 compressed blocks | Full FSE/tANS backend is not implemented; supported uncompressed/LZVN paths remain usable. |
| ZPAQ reader | Full ZPAQL virtual-machine execution is not implemented. |
| StuffIt X writer | Proprietary element-catalog/P2-varint writer is not implemented; the supported envelope shell is explicitly partial. |
| UMX writer | Full export-table + compact-index music encoding is not implemented; supported header/package shell is partial. |
| OLE2 application streams | CFB envelope creation does not synthesize Word/Excel/PowerPoint/MAPI/Catalog/Installer application databases. |
| Inno Setup | Some versions do not expose full individual-file extraction through the current reader. |
| RAR create | Fresh creation targets the implemented v4/v5 paths rather than every historical RAR writer version. |
| SFAR | LZX-compressed payload extraction remains limited. |
| Veeam | Only the documented/implemented summary/trailer path is claimed; undocumented chunk storage is not guessed. |
| Acronis/AOMEI/EaseUS/other reverse-engineered formats | Claims are limited to fields/structures evidenced by source, public binaries, tests, or clean-room analysis. Unknown encrypted/index layers remain unknown rather than invented. |

## 📦 Modern packaging details

| Format | Extensions | Read | Write | Reference | Notes |
| --- | --- | --- | --- | --- | --- |
| [AppImage](https://en.wikipedia.org/wiki/AppImage) | `.AppImage` | Yes | Yes | [AppImage spec](https://github.com/AppImage/AppImageSpec) | ELF stub + appended SquashFS; WORM delegates to supported filesystem image writer |
| [Snap](https://en.wikipedia.org/wiki/Snap_(software)) | `.snap` | Yes | — | [snapd](https://github.com/snapcore/snapd) | SquashFS package |
| [MSIX](https://en.wikipedia.org/wiki/MSIX) | `.msix`, `.msixbundle` | Yes | Yes | [MSIX](https://learn.microsoft.com/en-us/windows/msix/) | Unsigned fresh package output |
| ESD | `.esd` | Yes | — | [WIM/ESD](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wim-and-esd-windows-image-files-overview) | WIM-family compressed image |
| Split WIM | `.swm`, `.swmN` | Yes | — | [WIM](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/) | Multi-part WIM |
| [WACZ](https://specs.webrecorder.net/wacz/1.0.0/) | `.wacz` | Yes | — | [WACZ 1.0.0](https://specs.webrecorder.net/wacz/1.0.0/) | ZIP around WARC + package metadata |
| [Python Wheel](https://en.wikipedia.org/wiki/Wheel_(software)) | `.whl` | Yes | — | [PEP 427](https://peps.python.org/pep-0427/) | ZIP + dist-info |
| [Ruby Gem](https://en.wikipedia.org/wiki/RubyGems) | `.gem` | Yes | — | [Gem specification](https://guides.rubygems.org/specification-reference/) | TAR with compressed metadata/data members |
| Rust Crate | `.crate` | Yes | Yes | [Cargo registries](https://doc.rust-lang.org/cargo/reference/registries.html) | TAR.GZ with crate directory layout |

## 📚 Archive state model

CompressionWorkbench distinguishes **fresh creation** from **modifying an existing archive**. A correct writer does not imply safe in-place mutation.

| Capability | Meaning |
| --- | --- |
| `IArchiveFormatOperations` | Detect/list/extract/test surface |
| `IArchiveCreatable` | Can synthesize a new archive from entries → WORM |
| `IArchiveInPlaceModify` or equivalent supported modifier | Existing archive can be changed under its documented semantics → R/W |

The Ghost backup path is one example where the detailed implementation exposes modification semantics; most ordinary archive writers in this package remain WORM.

## 🔖 Versioning

This package is built against the repository's shared Core version. Consume a mutually compatible `Hawkynt.Compression.Core` version; release tooling determines the concrete package version rather than README predictions.

## 📚 API reference

<!-- API:BEGIN generated by Hawkynt/RepositoryTemplate/package-readme — edit the XML docs in source, not here -->

Every public and protected member of all 971 types, generated from the built assembly and its XML documentation, is in [REFERENCE.md](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.Archives/REFERENCE.md).

<!-- API:END -->

## 🔌 Dependencies

| Dependency | Role |
| --- | --- |
| [`Hawkynt.Compression.Core`](https://www.nuget.org/packages/Hawkynt.Compression.Core/) | Shared compression, entropy, transform, bit-I/O, and registry primitives |
| Native archive/compression libraries | **None required at runtime.** |

## ⚠️ Limitations

- WORM is intentionally not labeled R/W: creating a fresh valid archive is different from safely mutating an existing one.
- Proprietary formats may support correct extraction/creation without reproducing every encoder heuristic or private extension of the original vendor tool.
- Installer/package parsing is for inspection and archive operations; it does not emulate installation logic or execute package scripts.
- Internal round trips are useful but do not prove interoperability on their own; public specs, external tools, third-party corpora and regression tests provide independent evidence where available.
- Reverse-engineered proprietary structures are documented only to the depth evidenced by code/tests/reference binaries. Unknown structure is not filled with guesses.

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
