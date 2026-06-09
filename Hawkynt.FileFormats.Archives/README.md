# Hawkynt.FileFormats.Archives

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.FileFormats.Archives.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Archives/)
[![License](https://img.shields.io/badge/license-LGPL--3.0--or--later-blue)](https://www.gnu.org/licenses/lgpl-3.0.html)

> Pure-managed compression streams + archive containers extracted from
[CompressionWorkbench](https://github.com/Hawkynt/CompressionWorkbench). Sister package to
`Hawkynt.FileFormats.Audio` / `Hawkynt.FileFormats.FileSystems` / `Hawkynt.FileFormats.Images`,
all built on top of `Hawkynt.Compression.Core`.

The package bundles every archive-domain assembly into `lib/` and is the workbench's "swiss-army
knife" surface — install it and you can read or write practically any compression stream or
archive container in pure managed code, with no native runtime dependency on
`zlib` / `liblzma` / `libarchive` / `libbz2`.

## When to use this package

- You're writing a tool that needs to **enumerate / extract / write archives** in a wide range
  of formats from a single .NET process
- You want to **inspect installer payloads** (.msi / .nsis / Inno Setup / .appimage / .snap /
  .deb / .rpm / .apk / .ipa / .nupkg / .crx / .xpi / etc.) without running the installer
- You're processing **compression streams** without container framing — gzip / bzip2 / xz / zstd /
  lz4 / snappy / brotli / lzma / lzop and the long historical tail (`compress` / `pack` / `arc` /
  `lzh` / `quicklz` / `lzfse` / etc.)
- You're handling **ZIP-based document bundles** (Office Open XML — docx / xlsx / pptx;
  OpenDocument — odt / ods / odp; ePub; Comic-Book .cbz; KML .kmz; .vsdx)
- You're investigating **game / engine / asset packages** — Bethesda BSA / BA2, Unreal pak / .upk,
  Unity bundles, Quake / Doom WAD, Source Engine VPK, Mass Effect SFAR, RPG Maker RGSS, Ren'Py RPA,
  Godot PCK, Mortal Kombat MIX, Total Annihilation HPI / TFC, Master of Orion, etc.

Skip it when:

- You only need ZIP at default settings — `System.IO.Compression.ZipArchive` does that with the
  BCL's GZip / Deflate paths
- You need format-specific RAR / 7z encoder quality at parity with the original tool — those
  ship as native libraries with proprietary or LGPL fallback licensing concerns; this package
  prioritises read-coverage and clean-room license over pixel-identical output

## Quick start — extract a generic archive

```csharp
using Compression.Registry;       // FormatRegistry.DetectAndOpen
using FileFormat.Tar;             // ensures TarFormatDescriptor is registered
using FileFormat.SevenZip;        // ditto for 7z
// (every IFormatDescriptor in the package self-registers via the
//  source-generator at app start; just reference the assemblies you need)

using var input = File.OpenRead("payload.tar.gz");
var ops = FormatRegistry.DetectArchiveOperations(input);
foreach (var entry in ops.List(input))
  Console.WriteLine($"{entry.Name,-40}  {entry.Size,12:N0}");
```

## Quick start — round-trip a single compression stream

```csharp
using FileFormat.Brotli;

byte[] original   = File.ReadAllBytes("page.html");
byte[] compressed = new BrotliFormatDescriptor().Compress(original);
byte[] roundTrip  = new BrotliFormatDescriptor().Decompress(compressed);
Debug.Assert(original.SequenceEqual(roundTrip));
```

## Contents

State legend:
- **R** — read-only: `List` / `Extract` / `Test`. Cannot create new archives.
- **WORM** — Write-Once-Read-Many: read AND can synthesise a fresh archive from scratch
  (`IArchiveCreatable`), but cannot modify an existing archive in place.
- **R/W** — full read + write, including in-place add / replace / remove (`IArchiveInPlaceModify`).
  Currently no archive in this package is R/W; modifying an archive means "extract → modify →
  re-create from scratch".

#### Compression streams (single-file codecs without container framing)

| Format                   | State | Display name |
| ------------------------ | ----- | ------------ |
| `FileFormat.ApLib`       | WORM  | aPLib        |
| `FileFormat.Balz`        | WORM  | BALZ         |
| `FileFormat.Bcm`         | WORM  | BCM          |
| `FileFormat.BinHex`      | WORM  | BinHex       |
| `FileFormat.BriefLz`     | WORM  | BriefLZ      |
| `FileFormat.Brotli`      | WORM  | Brotli       |
| `FileFormat.Bsc`         | WORM  | BSC          |
| `FileFormat.Bzip2`       | WORM  | BZip2        |
| `FileFormat.Cmix`        | WORM  | cmix         |
| `FileFormat.Compress`    | WORM  | Unix Compress|
| `FileFormat.Crunch`      | WORM  | CP/M Crunch  |
| `FileFormat.Csc`         | WORM  | CSC          |
| `FileFormat.Density`     | WORM  | Density      |
| `FileFormat.Freeze`      | WORM  | Freeze       |
| `FileFormat.Gzip`        | WORM  | GZIP         |
| `FileFormat.IcePacker`   | WORM  | ICE Packer   |
| `FileFormat.Kwaj`        | WORM  | KWAJ         |
| `FileFormat.Lizard`      | WORM  | Lizard (LZ5) |
| `FileFormat.Lrzip`       | WORM  | Long Range Zip |
| `FileFormat.Lz4`         | WORM  | LZ4          |
| `FileFormat.Lzfse`       | WORM  | LZFSE        |
| `FileFormat.Lzg`         | WORM  | LZG          |
| `FileFormat.Lzham`       | WORM  | LZHAM        |
| `FileFormat.Lzip`        | WORM  | Lzip         |
| `FileFormat.Lzma`        | WORM  | LZMA         |
| `FileFormat.Lzop`        | WORM  | LZOP         |
| `FileFormat.Lzs`         | WORM  | LZS          |
| `FileFormat.Lzx`         | WORM  | LZX          |
| `FileFormat.MacBinary`   | WORM  | MacBinary    |
| `FileFormat.Mcm`         | WORM  | MCM          |
| `FileFormat.PackBits`    | WORM  | PackBits     |
| `FileFormat.Paq8`        | WORM  | PAQ8         |
| `FileFormat.PowerPacker` | WORM  | PowerPacker  |
| `FileFormat.Ppmd`        | WORM  | PPMd         |
| `FileFormat.QuickLz`     | WORM  | QuickLZ      |
| `FileFormat.RefPack`     | WORM  | RefPack / QFS |
| `FileFormat.Rnc`         | WORM  | RNC ProPack  |
| `FileFormat.Rzip`        | WORM  | Rzip         |
| `FileFormat.Snappy`      | WORM  | Snappy       |
| `FileFormat.Squeeze`     | WORM  | Squeeze      |
| `FileFormat.Szdd`        | WORM  | SZDD         |
| `FileFormat.UuEncoding`  | WORM  | UUEncoding   |
| `FileFormat.Xz`          | WORM  | XZ           |
| `FileFormat.YEnc`        | WORM  | yEnc         |
| `FileFormat.Yaz0`        | WORM  | Yaz0         |
| `FileFormat.Zlib`        | WORM  | Zlib         |
| `FileFormat.Zling`       | WORM  | Zling        |
| `FileFormat.Zstd`        | WORM  | Zstandard    |

#### Archive containers (multi-file)

| Format                 | State | Display name      |
| ---------------------- | ----- | ----------------- |
| `FileFormat.Ace`       | WORM  | ACE               |
| `FileFormat.AlZip`     | WORM  | ALZip             |
| `FileFormat.AppleSingle` | R   | AppleSingle       |
| `FileFormat.Ar`        | WORM  | AR                |
| `FileFormat.Arc`       | WORM  | ARC               |
| `FileFormat.Arj`       | WORM  | ARJ               |
| `FileFormat.Cab`       | WORM  | CAB               |
| `FileFormat.Cbr`       | WORM  | CBR               |
| `FileFormat.Cbz`       | WORM  | CBZ               |
| `FileFormat.Chm`       | WORM  | CHM               |
| `FileFormat.CompactPro` | WORM | Compact Pro      |
| `FileFormat.Cpio`      | WORM  | CPIO              |
| `FileFormat.DiskDoubler` | WORM | DiskDoubler      |
| `FileFormat.Dms`       | WORM  | DMS               |
| `FileFormat.Esd`       | R     | ESD               |
| `FileFormat.FreeArc`   | WORM  | FreeArc           |
| `FileFormat.Ha`        | WORM  | HA                |
| `FileFormat.IffCdaf`   | WORM  | IFF CDAF          |
| `FileFormat.Lbr`       | WORM  | LBR               |
| `FileFormat.LhF`       | WORM  | LhF (LhFloppy)    |
| `FileFormat.Lzh`       | WORM  | LZH               |
| `FileFormat.PackDisk`  | WORM  | PackDisk (Amiga)  |
| `FileFormat.PackIt`    | WORM  | PackIt            |
| `FileFormat.Rar`       | WORM  | RAR               |
| `FileFormat.Sar`       | WORM  | SAR               |
| `FileFormat.SevenZip`  | WORM  | 7z                |
| `FileFormat.Shar`      | WORM  | SHAR              |
| `FileFormat.Spark`     | WORM  | Spark             |
| `FileFormat.SplitFile` | WORM  | Split File (.001) |
| `FileFormat.Sqx`       | WORM  | SQX               |
| `FileFormat.StuffIt`   | WORM  | StuffIt           |
| `FileFormat.StuffItX`  | WORM  | StuffIt X         |
| `FileFormat.Swm`       | R     | Split WIM         |
| `FileFormat.Tar`       | WORM  | TAR               |
| `FileFormat.Uharc`     | WORM  | UHARC             |
| `FileFormat.Wim`       | WORM  | WIM               |
| `FileFormat.Wrapster`  | WORM  | Wrapster          |
| `FileFormat.Xar`       | WORM  | XAR               |
| `FileFormat.Zip`       | WORM  | ZIP               |
| `FileFormat.Zoo`       | WORM  | ZOO               |
| `FileFormat.Zpaq`      | WORM  | ZPAQ              |

#### Software-package containers

| Format                       | State | Display name                      |
| ---------------------------- | ----- | --------------------------------- |
| `FileFormat.AndroidBundle`   | R     | Android App Bundle / split-APK    |
| `FileFormat.AndroidOta`      | R     | Android OTA payload               |
| `FileFormat.Apk`             | WORM  | APK                               |
| `FileFormat.ApkNativeLibs`   | R     | APK native libraries              |
| `FileFormat.AppImage`        | R     | AppImage                          |
| `FileFormat.Appx`            | WORM  | APPX                              |
| `FileFormat.Crate`           | R     | Rust Crate                        |
| `FileFormat.Crx`             | WORM  | CRX (Chrome extension)            |
| `FileFormat.Deb`             | WORM  | DEB                               |
| `FileFormat.Ear`             | WORM  | EAR                               |
| `FileFormat.Gem`             | R     | Ruby Gem                          |
| `FileFormat.InnoSetup`       | WORM  | Inno Setup                        |
| `FileFormat.Ipa`             | WORM  | IPA                               |
| `FileFormat.Jar`             | WORM  | JAR                               |
| `FileFormat.Msi`             | WORM  | MSI (OLE Compound File)           |
| `FileFormat.Msix`            | WORM  | MSIX                              |
| `FileFormat.Nsis`            | WORM  | NSIS                              |
| `FileFormat.NuPkg`           | WORM  | NuPkg                             |
| `FileFormat.Rpm`             | WORM  | RPM                               |
| `FileFormat.Snap`            | R     | Snap package                      |
| `FileFormat.War`             | WORM  | WAR                               |
| `FileFormat.Wheel`           | R     | Python Wheel                      |
| `FileFormat.Xpi`             | WORM  | XPI (Mozilla extension)           |

#### Office Open XML / OpenDocument / web archive zip-bundles

| Format            | State | Display name   |
| ----------------- | ----- | -------------- |
| `FileFormat.Docx` | WORM  | DOCX           |
| `FileFormat.Xlsx` | WORM  | XLSX           |
| `FileFormat.Pptx` | WORM  | PPTX           |
| `FileFormat.Odt`  | WORM  | ODT            |
| `FileFormat.Ods`  | WORM  | ODS            |
| `FileFormat.Odp`  | WORM  | ODP            |
| `FileFormat.Vsdx` | WORM  | Visio Drawing  |
| `FileFormat.Epub` | WORM  | EPUB           |
| `FileFormat.Kmz`  | WORM  | KMZ            |
| `FileFormat.Maff` | WORM  | MAFF           |
| `FileFormat.Wacz` | R     | WACZ           |
| `FileFormat.Warc` | WORM  | WARC           |
| `FileFormat.Wbn`  | R     | Web Bundle     |

#### Game / engine / install / runtime / Amiga / vintage long-tail formats

| Format                  | State | Display name                       |
| ----------------------- | ----- | ---------------------------------- |
| `FileFormat.Afs`        | WORM  | Sega AFS                           |
| `FileFormat.Ampk`       | WORM  | AMPK (Amiga Pack)                  |
| `FileFormat.Ba2`        | WORM  | Bethesda Archive v2                |
| `FileFormat.Big`        | WORM  | BIG (Westwood / EA)                |
| `FileFormat.Bsa`        | WORM  | BSA                                |
| `FileFormat.Dzip`       | WORM  | Bloodlines DZIP                    |
| `FileFormat.Gar`        | WORM  | Nintendo 3DS GAR                   |
| `FileFormat.Gob`        | WORM  | LucasArts GOB                      |
| `FileFormat.GodotPck`   | WORM  | Godot PCK                          |
| `FileFormat.Grp`        | WORM  | GRP (Build engine)                 |
| `FileFormat.Hog`        | WORM  | HOG (Descent)                      |
| `FileFormat.Hpi`        | WORM  | Total Annihilation HPI             |
| `FileFormat.Lfd`        | WORM  | LucasArts LFD                      |
| `FileFormat.Mhk`        | WORM  | Cyan Mohawk                        |
| `FileFormat.Mix`        | WORM  | Westwood MIX                       |
| `FileFormat.Mpq`        | WORM  | MPQ (Blizzard)                     |
| `FileFormat.Narc`       | WORM  | Nintendo NARC                      |
| `FileFormat.Nds`        | WORM  | NDS (Nintendo DS ROM)              |
| `FileFormat.Nsa`        | WORM  | NSA (NScripter)                    |
| `FileFormat.Pak`        | WORM  | PAK (Quake)                        |
| `FileFormat.Pbp`        | WORM  | PSP PBP archive                    |
| `FileFormat.Psarc`      | WORM  | PSARC (Sony)                       |
| `FileFormat.Rgss`       | WORM  | RPG Maker RGSSAD                   |
| `FileFormat.Rpa`        | R     | Ren'Py Archive                     |
| `FileFormat.Sarc`       | WORM  | Nintendo SARC                      |
| `FileFormat.Sfar`       | R     | BioWare SFAR (Mass Effect)         |
| `FileFormat.Slf`        | WORM  | Sir-Tech SLF (Jagged Alliance)     |
| `FileFormat.Swf`        | WORM  | SWF (Flash)                        |
| `FileFormat.Tfc`        | WORM  | Mass Effect TFC                    |
| `FileFormat.Tnef`       | WORM  | MS-TNEF (winmail.dat)              |
| `FileFormat.U8`         | WORM  | Nintendo U8                        |
| `FileFormat.Umx`        | WORM  | Unreal Music (UMX)                 |
| `FileFormat.UnityBundle` | R    | Unity Asset Bundle                 |
| `FileFormat.UnrealPak`  | R     | Unreal Pak                         |
| `FileFormat.Upx`        | R     | UPX-packed executable              |
| `FileFormat.Vpk`        | WORM  | VPK (Steam)                        |
| `FileFormat.Vpp`        | WORM  | Volition Package (RF1)             |
| `FileFormat.VppV2`      | WORM  | Volition VPP v2 (Saint's Row 2)    |
| `FileFormat.Wad`        | WORM  | WAD (Doom)                         |
| `FileFormat.Wad2`       | WORM  | WAD2 / WAD3 (Quake)                |
| `FileFormat.Ypf`        | WORM  | YukaScript YPF                     |
| `FileFormat.Zap`        | WORM  | ZAP (Amiga Disk Archiver)          |

#### Backup-software disk images

Whole-system / partition backups from consumer + enterprise backup suites. Several were
reverse-engineered directly from vendor binaries (no published spec) — the descriptor for
each one names exactly which fields are decoded versus documented-TODO. R/W formats survive
their own round-trip; WORM formats can produce a fresh image that the same reader walks back.

| Format                  | State | Display name                                                  |
| ----------------------- | ----- | ------------------------------------------------------------- |
| `FileFormat.Acronis`    | R     | Acronis True Image — classic `.tib` (FileMeta chain walk + ItemCommon name) |
| `FileFormat.AcronisTibx`| R     | Acronis True Image — modern `.tibx` (Stage-1 page-zero header + metadata) |
| `FileFormat.Aomei`      | WORM  | AOMEI Backupper `.adi` / `.afi` (BIFH/BIFT + BR\_STANDARD\_HEADER envelope) |
| `FileFormat.AppleSparse`| R     | Apple sparseimage + sparsebundle (Time Machine / hdiutil)     |
| `FileFormat.Bkf`        | R     | Microsoft NTBackup `.bkf` (MTF Tape Format)                   |
| `FileFormat.EaseUs`     | R     | EaseUS Todo Backup `.pbd` (R/O chunk stream)                  |
| `FileFormat.Ghost`      | R/W   | Norton Ghost 3.0 → 11.x (Fast LZ Z1 + zlib Z3-Z9 + CRC-16 stream cipher) |
| `FileFormat.Macrium`    | R/W   | Macrium Reflect X `.mrimgx` (open MIT-licensed spec)          |
| `FileFormat.Paragon`    | WORM  | Paragon Backup & Recovery `.pbf` (CWBP write-once)            |
| `FileFormat.Partclone`  | R     | Clonezilla partclone (`.img` partition clone)                 |
| `FileFormat.Veeam`      | R     | Veeam B&R `.vbk` / `.vib` / `.vrb` (Stage-1 OibSummary trailer only) |

## Detailed format reference

The tables below mirror the canonical "what does each format actually support" reference from
the source repo. Each row links to the upstream spec the implementation was validated against
plus a one-line note covering scope / limitations. WORM = can produce a fresh archive that
round-trips; **`-`** = read-only.

### Archive containers (canonical)

| Format                                                                | Extensions      | Read | Write       | Reference                                                                                                                                  | Notes                                                                                                                                                       |
| --------------------------------------------------------------------- | --------------- | ---- | ----------- | ------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [ZIP](https://en.wikipedia.org/wiki/ZIP_(file_format))                | `.zip`          | Yes  | Yes         | [APPNOTE.TXT](https://pkwaredownloads.blob.core.windows.net/pem/APPNOTE.txt)                                                               | Store, Deflate, Deflate64, Shrink, Reduce, Implode, BZip2, LZMA, PPMd, Zstd, AES                                                                            |
| [RAR](https://en.wikipedia.org/wiki/RAR_(file_format))                | `.rar`          | Yes  | Yes (v4/v5) | [rarlab technote](https://www.rarlab.com/technote.htm)                                                                                     | v1-v5 decoders, solid, multi-volume, encryption, recovery                                                                                                   |
| [7z](https://en.wikipedia.org/wiki/7z)                                | `.7z`           | Yes  | Yes         | [7-Zip format](https://www.7-zip.org/7z.html)                                                                                              | LZMA/LZMA2, Deflate, BZip2, PPMd, BCJ/BCJ2, AES-256, multi-volume                                                                                           |
| [TAR](https://en.wikipedia.org/wiki/Tar_(computing))                  | `.tar`          | Yes  | Yes         | [POSIX ustar](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html)                                                         | POSIX/GNU/PAX, multi-volume                                                                                                                                 |
| [CAB](https://en.wikipedia.org/wiki/Cabinet_(file_format))            | `.cab`          | Yes  | Yes         | [MS-CAB](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cab/)                                                            | MSZIP, LZX, Quantum                                                                                                                                         |
| [LZH/LHA](https://en.wikipedia.org/wiki/LHA_(file_format))            | `.lzh`,`.lha`   | Yes  | Yes         | [LHA archive format](http://www.math.sci.hiroshima-u.ac.jp/m-mat/MT/hamamura-home/lha-en.html)                                             | lh0-lh7, lzs, lh1-lh3 (adaptive Huffman), pm0-pm2                                                                                                           |
| [ARJ](https://en.wikipedia.org/wiki/ARJ)                              | `.arj`          | Yes  | Yes         | [ARJ technical](http://www.arjsoftware.com/)                                                                                               | Methods 0-4, garble encryption                                                                                                                              |
| [ARC](https://en.wikipedia.org/wiki/ARC_(file_format))                | `.arc`          | Yes  | Yes         | [ARC format](http://fileformats.archiveteam.org/wiki/ARC_(compression_format))                                                             | Methods 0-9 (RLE, LZW, Squeeze, Huffman)                                                                                                                    |
| [ZOO](https://en.wikipedia.org/wiki/Zoo_(file_format))                | `.zoo`          | Yes  | Yes         | [zoo format](http://fileformats.archiveteam.org/wiki/ZOO)                                                                                  | LZW, LZH                                                                                                                                                    |
| [ACE](https://en.wikipedia.org/wiki/ACE_(compressed_file_format))     | `.ace`          | Yes  | Yes         | [ACE unofficial spec](https://github.com/droe/acefile/blob/master/acefile.py)                                                              | ACE 1.0/2.0, solid, sound/picture filters, Blowfish, recovery                                                                                               |
| SQX                                                                   | `.sqx`          | Yes  | Yes         | [SQX disassembly](https://encode.su/threads/1290-SQX-(by-SpeedProject))                                                                    | LZH, multimedia, audio, solid, AES-128, recovery                                                                                                            |
| [CPIO](https://en.wikipedia.org/wiki/Cpio)                            | `.cpio`         | Yes  | Yes         | [cpio(5)](https://www.freebsd.org/cgi/man.cgi?query=cpio&sektion=5)                                                                        | Binary, odc, newc, CRC                                                                                                                                      |
| [AR](https://en.wikipedia.org/wiki/Ar_(Unix))                         | `.ar`           | Yes  | Yes         | [ar(5)](https://www.freebsd.org/cgi/man.cgi?query=ar&sektion=5)                                                                            | Unix archive                                                                                                                                                |
| [WIM](https://en.wikipedia.org/wiki/Windows_Imaging_Format)           | `.wim`          | Yes  | Yes         | [Imagex WIM format](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/)                                               | LZX, XPRESS                                                                                                                                                 |
| [RPM](https://en.wikipedia.org/wiki/RPM_Package_Manager)              | `.rpm`          | Yes  | Yes         | [RPM spec](https://rpm-software-management.github.io/rpm/manual/format.html)                                                               | CPIO payload                                                                                                                                                |
| [DEB](https://en.wikipedia.org/wiki/Deb_(file_format))                | `.deb`          | Yes  | Yes         | [deb(5)](https://man7.org/linux/man-pages/man5/deb.5.html)                                                                                 | AR+TAR with gz/xz/zst/bz2                                                                                                                                   |
| [Shar](https://en.wikipedia.org/wiki/Shar)                            | `.shar`         | Yes  | Yes         | [GNU sharutils](https://www.gnu.org/software/sharutils/)                                                                                   | Shell archive                                                                                                                                               |
| PAK                                                                   | `.pak`          | Yes  | Yes         | [PAK spec](http://fileformats.archiveteam.org/wiki/PAK)                                                                                    | ARC-compatible                                                                                                                                              |
| [HA](https://en.wikipedia.org/wiki/HA_(file_format))                  | `.ha`           | Yes  | Yes         | [HA specification](http://fileformats.archiveteam.org/wiki/HA)                                                                             | HSC/ASC arithmetic coding                                                                                                                                   |
| [ZPAQ](https://en.wikipedia.org/wiki/ZPAQ)                            | `.zpaq`         | Yes  | Yes         | [ZPAQ spec PDF](https://mattmahoney.net/dc/zpaq206.pdf)                                                                                    | Context mixing, journaling                                                                                                                                  |
| [StuffIt](https://en.wikipedia.org/wiki/StuffIt)                      | `.sit`          | Yes  | Yes         | [libxad sit.c](https://github.com/MacPaw/XADMaster)                                                                                        | Multiple methods                                                                                                                                            |
| StuffIt X                                                             | `.sitx`         | Yes  | Yes         | [XADMaster StuffItX](https://github.com/MacPaw/XADMaster)                                                                                  | Detection-only; WORM emits a valid `StuffIt!` envelope (proprietary element-stream writer not implemented)                                                  |
| [SquashFS](https://en.wikipedia.org/wiki/SquashFS)                    | `.sqfs`         | Yes  | Yes         | [SquashFS 4.0 spec](https://dr-emann.github.io/squashfs/)                                                                                  | Filesystem image                                                                                                                                            |
| [CramFS](https://en.wikipedia.org/wiki/Cramfs)                        | `.cramfs`       | Yes  | Yes         | [Linux `fs/cramfs/`](https://github.com/torvalds/linux/tree/master/fs/cramfs)                                                              | Filesystem image                                                                                                                                            |
| [NSIS](https://en.wikipedia.org/wiki/Nullsoft_Scriptable_Install_System) | `.exe`        | Yes  | Yes         | [NSIS wiki](https://nsis.sourceforge.io/Docs/)                                                                                             | Installer extraction + WORM emits overlay-only data (no PE stub)                                                                                            |
| Inno Setup                                                            | `.exe`          | Yes  | Yes         | [innounp](https://sourceforge.net/projects/innounp/)                                                                                       | Installer extraction + WORM emits signature header (no PE stub)                                                                                             |
| [DMS](https://en.wikipedia.org/wiki/Disk_Masher_System)               | `.dms`          | Yes  | Yes         | [xDMS source](https://github.com/markrabjohn/xDMS)                                                                                         | Amiga disk archiver                                                                                                                                         |
| [LZX (Amiga)](https://en.wikipedia.org/wiki/LZX)                      | `.lzx`          | Yes  | Yes         | [Amiga LZX format](http://fileformats.archiveteam.org/wiki/LZX)                                                                            | Amiga LZX                                                                                                                                                   |
| [Compact Pro](https://en.wikipedia.org/wiki/Compact_Pro)              | `.cpt`          | Yes  | Yes         | [XADMaster cpt.c](https://github.com/MacPaw/XADMaster)                                                                                     | Classic Mac format                                                                                                                                          |
| Spark                                                                 | `.spark`        | Yes  | Yes         | [RISC OS Spark](http://fileformats.archiveteam.org/wiki/Spark)                                                                             | RISC OS format                                                                                                                                              |
| [LBR](https://en.wikipedia.org/wiki/LU_(software))                    | `.lbr`          | Yes  | Yes         | [CP/M LBR](http://www.gaby.de/cpm/manuals/archive/lbr.txt)                                                                                 | CP/M format                                                                                                                                                 |
| UHARC                                                                 | `.uha`          | Yes  | Yes         | [UHARC docs](http://www.uharc.com/)                                                                                                        | LZP compression                                                                                                                                             |
| [WAD (Doom)](https://en.wikipedia.org/wiki/Doom_WAD)                  | `.wad`          | Yes  | Yes         | [Doom Wiki WAD](https://doomwiki.org/wiki/WAD)                                                                                             | Doom WAD format                                                                                                                                             |
| WAD2/WAD3                                                             | `.wad`          | Yes  | Yes         | [Quake Wiki WAD](https://quakewiki.org/wiki/.wad)                                                                                          | Quake/Half-Life texture archive                                                                                                                             |
| [XAR](https://en.wikipedia.org/wiki/Xar_(archiver))                   | `.xar`          | Yes  | Yes         | [XAR on-disk format](https://github.com/mackyle/xar/wiki/xarformat)                                                                        | Apple `.pkg` (zlib TOC)                                                                                                                                     |
| [ALZip](https://en.wikipedia.org/wiki/ALZip)                          | `.alz`          | Yes  | Yes         | [ALZ format](http://fileformats.archiveteam.org/wiki/ALZ)                                                                                  | Korean archive (Deflate)                                                                                                                                    |
| VPK                                                                   | `.vpk`          | Yes  | Yes         | [Valve VPK](https://developer.valvesoftware.com/wiki/VPK_(file_format))                                                                    | Valve game archive                                                                                                                                          |
| BSA                                                                   | `.bsa`          | Yes  | Yes         | [BSA format](https://en.uesp.net/wiki/Skyrim_Mod:File_Formats/BSA)                                                                         | Bethesda game archive (Morrowind / Oblivion / Skyrim)                                                                                                       |
| BA2                                                                   | `.ba2`          | Yes  | Yes         | [BA2 (BTDX)](https://en.uesp.net/wiki/Skyrim_Mod:File_Formats/BA2)                                                                         | Bethesda Archive v2 (Fallout 4 / Skyrim SE), GNRL subtype only — Bob Jenkins lookup3 hash                                                                   |
| [MPQ](https://en.wikipedia.org/wiki/MPQ)                              | `.mpq`          | Yes  | Yes         | [ZezulaMPQ docs](https://github.com/ladislav-zezula/StormLib)                                                                              | Blizzard — WORM v1 with stored entries, encrypted hash+block tables, self-referential `(listfile)`                                                          |
| [GRP](https://moddingwiki.shikadi.net/wiki/GRP_(Build)_Format)        | `.grp`          | Yes  | Yes         | [BUILD Engine docs](https://moddingwiki.shikadi.net/wiki/GRP_(Build)_Format)                                                               | BUILD Engine (Duke Nukem 3D)                                                                                                                                |
| [HOG](https://en.wikipedia.org/wiki/HOG_(file_format))                | `.hog`          | Yes  | Yes         | [Descent HOG](http://descent.wikia.com/wiki/HOG)                                                                                           | Descent game archive                                                                                                                                        |
| BIG                                                                   | `.big`          | Yes  | Yes         | [EA BIG format](http://wiki.xentax.com/index.php/EA_BIG)                                                                                   | EA Games (C&C, FIFA)                                                                                                                                        |
| Godot PCK                                                             | `.pck`          | Yes  | Yes         | [Godot PCK spec](https://docs.godotengine.org/en/stable/development/file_formats/pck.html)                                                 | Godot Engine resource pack                                                                                                                                  |
| [WARC](https://en.wikipedia.org/wiki/Web_ARChive)                     | `.warc`         | Yes  | Yes         | [ISO 28500](https://iipc.github.io/warc-specifications/)                                                                                   | Web archive — WORM emits one `resource` record per input file                                                                                               |
| NDS                                                                   | `.nds`          | Yes  | Yes         | [GBATEK NDS](https://problemkaputt.de/gbatek.htm)                                                                                          | Nintendo DS ROM — WORM emits valid NitroFS (no ARM9/ARM7 boot code)                                                                                         |
| NSA                                                                   | `.nsa`          | Yes  | Yes         | [NScripter docs](https://www.nscripter.com/)                                                                                               | NScripter — WORM writes stored entries (compression type 0)                                                                                                 |
| SAR                                                                   | `.sar`          | Yes  | Yes         | [NScripter docs](https://www.nscripter.com/)                                                                                               | NScripter — uncompressed variant of NSA                                                                                                                     |
| PackIt                                                                | `.pit`          | Yes  | Yes         | [XADMaster packit.c](https://github.com/MacPaw/XADMaster)                                                                                  | Classic Mac format — WORM emits stored entries                                                                                                              |
| DiskDoubler                                                           | `.dd`           | Yes  | Yes         | [XADMaster DD](https://github.com/MacPaw/XADMaster)                                                                                        | Classic Mac compression — WORM stores data fork (method 0)                                                                                                  |
| MSI                                                                   | `.msi`          | Yes  | Yes         | [MS-CFB](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/)                                                            | OLE Compound File — WORM produces a CFB envelope (not a functional Installer DB)                                                                            |
| [PDF](https://en.wikipedia.org/wiki/PDF)                              | `.pdf`          | Yes  | Yes         | [ISO 32000](https://www.iso.org/standard/75839.html)                                                                                       | Image extraction + WORM via file attachments (EmbeddedFiles) — any file type round-trips                                                                    |
| [TNEF](https://en.wikipedia.org/wiki/Transport_Neutral_Encapsulation_Format) | `.tnef`,`.dat` | Yes | Yes      | [MS-OXTNEF](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxtnef/)                                              | Outlook `winmail.dat`                                                                                                                                       |
| Split File                                                            | `.001`          | Yes  | Yes         | —                                                                                                                                          | Multi-part file joining/splitting                                                                                                                           |
| FreeArc                                                               | `.arc`          | Yes  | Yes         | [FreeArc source](https://github.com/Bulat-Ziganshin/FreeArc)                                                                               | FreeArc archive                                                                                                                                             |
| [CHM](https://en.wikipedia.org/wiki/Microsoft_Compiled_HTML_Help)     | `.chm`          | Yes  | Yes         | [CHM file format](https://archive.org/details/chmspec)                                                                                     | MS Compiled HTML Help — WORM stores files in section 0 (uncompressed); LZX compression available via options                                                |
| Wrapster                                                              | -               | Yes  | Yes         | [XADMaster wrapster.c](https://github.com/MacPaw/XADMaster)                                                                                | MP3 wrapper archive                                                                                                                                         |
| LhF                                                                   | `.lhf`          | Yes  | Yes         | [XADMaster](https://github.com/MacPaw/XADMaster)                                                                                           | Amiga LhFloppy disk (LZH-compressed tracks)                                                                                                                 |
| ZAP                                                                   | `.zap`          | Yes  | Yes         | [XADMaster](https://github.com/MacPaw/XADMaster)                                                                                           | Amiga disk archiver — WORM writes stored tracks                                                                                                             |
| PackDisk                                                              | `.pdsk`         | Yes  | Yes         | [XADMaster](https://github.com/MacPaw/XADMaster)                                                                                           | Amiga PackDisk — WORM writes stored tracks. Same writer covers DCS / xDisk / xMash via different magics.                                                    |
| AMPK                                                                  | -               | Yes  | Yes         | [XADMaster](https://github.com/MacPaw/XADMaster)                                                                                           | Amiga AMPK — WORM emits stored entries                                                                                                                      |
| IFF-CDAF                                                              | -               | Yes  | Yes         | [IFF spec](http://fileformats.archiveteam.org/wiki/IFF)                                                                                    | IFF-CDAF archive — WORM emits stored entries                                                                                                                |
| UMX                                                                   | `.umx`          | Yes  | Yes         | [Beyond Unreal wiki](https://wiki.beyondunreal.com/Legacy:Package_File_Format)                                                             | Unreal package — WORM emits valid header (detection-only)                                                                                                   |
| PSARC                                                                 | `.psarc`        | Yes  | Yes         | [PSARC spec](https://www.psdevwiki.com/ps3/PlayStation_archive_(PSARC))                                                                    | Sony PlayStation archive (PS3/PS4/Vita) — zlib block compression (LZMA / encrypted-TOC rejected)                                                            |
| MIX                                                                   | `.mix`          | Yes  | Yes         | [XCC / OpenRA](https://github.com/OpenRA/OpenRA/blob/bleed/OpenRA.Mods.Cnc/FileSystem/MixFile.cs)                                          | Westwood C&C / Red Alert 1 — hash-keyed, names not stored; reader synthesizes `<HEX>.bin`                                                                   |
| VPP                                                                   | `.vpp`          | Yes  | Yes         | [Volition file format wiki](http://www.redfactionwiki.com/wiki/RF1:VPP_File_Format)                                                        | Volition Package v1 (Red Faction 1 / Summoner) — 2048-byte aligned                                                                                          |
| PBP                                                                   | `.pbp`          | Yes  | Yes         | [PSP PBP layout](https://www.psdevwiki.com/psp/PBP)                                                                                        | PlayStation Portable EBOOT — 8 fixed sections (PARAM.SFO / ICON0.PNG / DATA.PSP / DATA.PSAR …)                                                              |
| GOB                                                                   | `.gob`,`.goo`   | Yes  | Yes         | [Lucasarts GOB](https://www.moddb.com/games/star-wars-jedi-knight-jedi-academy/tutorials/gob-pak-format-explained)                         | LucasArts Jedi Knight / Outlaws — TOC-at-end, version 0x14 / 0x20                                                                                           |
| LFD                                                                   | `.lfd`          | Yes  | Yes         | [LFD resource format](https://web.archive.org/web/20140805170029/http://www.lucasforums.com/showthread.php?t=131803)                       | LucasArts X-Wing / TIE Fighter — 4-char Type + 8-char Name; auto-emits valid `RMAP` index                                                                   |
| PFS0                                                                  | `.nsp`,`.pfs0`  | Yes  | Yes         | [Switchbrew PFS0](https://switchbrew.org/wiki/NCA_Format#PFS0)                                                                             | Nintendo Switch PartitionFS / NSP package — alphabetically sorted; rejects HFS0 sibling                                                                     |
| SLF                                                                   | `.slf`          | Yes  | Yes         | [JA2-Stracciatella SLF](https://github.com/ja2-stracciatella/ja2-stracciatella/blob/master/src/sgp/SlfReader.cc)                           | Sir-Tech library (Jagged Alliance 2) — 532-byte header / 280-byte entries; tombstoned (state=0xFF) entries skipped                                          |
| HPI                                                                   | `.hpi`,`.ufo`,`.ccx`,`.gp3` | Yes | Yes  | [TA HPI format](https://units.tauniverse.com/tutorials/tadesign/tutorials/hpi.htm)                                                         | Total Annihilation HAPI — zlib subset only (encrypted HeaderKey≠0 + LZ77 chunks rejected); 64KB SQSH framing                                                |
| SARC                                                                  | `.sarc`,`.pack`,`.bars` | Yes | Yes      | [3DBrew SARC](https://www.3dbrew.org/wiki/SARC)                                                                                            | Nintendo Sorted Archive (Wii U / 3DS / Switch) — endian-aware reads (BOM); LE writes; hash-sorted with key 0x65                                             |
| AFS                                                                   | `.afs`          | Yes  | Yes         | [AFS format wiki](http://wiki.xentax.com/index.php/SEGA_Athena_Filesystem_(AFS))                                                           | Sega Athena Filesystem (Dreamcast / PS2 / GameCube) — optional metadata block; 0x800 alignment                                                              |
| NARC                                                                  | `.narc`,`.carc` | Yes  | Yes         | [GBATEK NARC](https://problemkaputt.de/gbatek.htm#dscartridgenitrosdkbinaries)                                                             | Nintendo DS Archive Resource Compound — flat BTNF tree; BTAF + BTNF + GMIF                                                                                  |
| SFAR                                                                  | `.sfar`         | Yes  | -           | [ME3Tweaks SFAR docs](https://me3tweaks.com/me3tweaks-help-and-info/me3-modding-assistant/sfar-files)                                      | BioWare Mass Effect 3 DLC — 5-byte LE integers; SHA-1 path hashes; LZX blocks not yet decompressed                                                          |
| PSF                                                                   | `.psf`,`.minipsf`,`.ssf`,`.dsf`,`.gsf`,`.usf`,`.2sf`,`.ncsf`,`.snsf`,`.qsf` | Yes | Yes | [PSF spec (Corlett)](https://web.archive.org/web/20060212232218/http://wiki.neillcorlett.com/PSFFormat) | Portable Sound Format — chiptune container (zlib program + tag block); pseudo-archive entries: `header.bin / reserved.bin / program.bin / tags.txt`         |
| MHK                                                                   | `.mhk`          | Yes  | Yes         | [ScummVM Mohawk](https://github.com/scummvm/scummvm/tree/master/engines/mohawk)                                                            | Cyan Mohawk archive (Myst Masterpiece / Riven / Cosmic Osmo / Living Books) — outer MHWK + inner RSRC (big-endian)                                          |
| YPF                                                                   | `.ypf`          | Yes  | Yes         | [crass tools](https://github.com/regomne/crass)                                                                                            | YukaScript engine archive (Yu-No, Iyashi VN engine) — v480 only; raw ASCII names (engine obfuscation skipped)                                               |
| U8                                                                    | `.u8`,`.arc`    | Yes  | Yes         | [U8 archive notes](https://wiki.tockdom.com/wiki/U8_(File_Format))                                                                         | Nintendo Wii / Wii U / 3DS archive — big-endian, 3-byte name offset, parent/end-index directory tree                                                        |
| AKB                                                                   | `.akb`          | Yes  | Yes         | [vgmstream AKB](https://github.com/vgmstream/vgmstream/blob/master/src/meta/akb.c)                                                         | Square Enix audio bank — raw audio bytes surfaced (no codec decode), `metadata.ini` carries header info                                                     |
| AWB / AFS2                                                            | `.awb`,`.acb`   | Yes  | Yes         | [VGMToolbox AFS2](http://wiki.xentax.com/index.php/CRI_Wave_Bank)                                                                          | CRI Audio Wave Bank — endian-agnostic offset width; alignment-aware (default 0x20); raw bytes per cue ID                                                    |
| Web Bundle                                                            | `.wbn`          | Yes  | -           | [draft-yasskin-wpack-bundled-exchanges](https://datatracker.ietf.org/doc/draft-yasskin-wpack-bundled-exchanges/)                           | Bundled HTTP Exchanges — pseudo-archive (`FULL.wbn + metadata.ini`); minimal CBOR walker (no full decode)                                                   |
| LRZIP                                                                 | `.lrz`          | Yes  | Yes         | [Long Range Zip](https://github.com/ckolivas/lrzip)                                                                                        | Single-stream LZMA wrapper (LZO/BZIP2/GZIP/ZPAQ subtypes rejected); 5-byte LZMA preamble + raw bounded stream                                               |
| GAR                                                                   | `.gar`          | Yes  | Yes         | [3DBrew GAR](https://www.3dbrew.org/wiki/GAR)                                                                                              | Nintendo 3DS Generic Asset Resource — type-grouped layout where files sharing an extension share a type entry                                               |
| ARSC                                                                  | `.arsc`         | Yes  | -           | [AOSP ResourceTypes.h](https://android.googlesource.com/platform/frameworks/base/+/master/libs/androidfw/include/androidfw/ResourceTypes.h) | Android compiled resource table (inside APK); pseudo-archive with package + string-pool counts; tolerant chunk walker                                       |

### ZIP-derived containers

All delegate to the ZIP reader/writer. WORM (`Yes`) means a fresh container can be produced
with the correct internal layout for that flavour.

| Format                                                                  | Extensions      | Read | Write | Reference                                                                                                          | Notes                                                                            |
| ----------------------------------------------------------------------- | --------------- | ---- | ----- | ------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------- |
| [JAR](https://en.wikipedia.org/wiki/JAR_(file_format))                  | `.jar`          | Yes  | Yes   | [JAR spec](https://docs.oracle.com/en/java/javase/21/docs/specs/jar/jar.html)                                      | Java archive                                                                     |
| WAR                                                                     | `.war`          | Yes  | Yes   | [Java EE WAR](https://docs.oracle.com/javaee/7/tutorial/packaging003.htm)                                          | Java web archive                                                                 |
| EAR                                                                     | `.ear`          | Yes  | Yes   | [Java EE EAR](https://docs.oracle.com/javaee/7/tutorial/packaging004.htm)                                          | Java enterprise archive                                                          |
| [APK](https://en.wikipedia.org/wiki/Apk_(file_format))                  | `.apk`          | Yes  | Yes   | [Android APK](https://source.android.com/docs/core/runtime/jit-compiler)                                           | Android package                                                                  |
| [IPA](https://en.wikipedia.org/wiki/.ipa)                               | `.ipa`          | Yes  | Yes   | [Apple IPA bundle](https://developer.apple.com/documentation/)                                                     | iOS package                                                                      |
| APPX                                                                    | `.appx`,`.msix` | Yes  | Yes   | [MS-APPXPKG](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/)                                           | Windows package                                                                  |
| [XPI](https://en.wikipedia.org/wiki/XPInstall)                          | `.xpi`          | Yes  | Yes   | [Mozilla XPI](https://developer.mozilla.org/en-US/docs/Mozilla/Tech/XPI)                                           | Firefox extension                                                                |
| CRX                                                                     | `.crx`          | Yes  | Yes   | [Chrome CRX3](https://developer.chrome.com/docs/extensions/mv3/linux_hosting/)                                     | Chrome extension — WORM emits unsigned CRX3 envelope (browser rejects signature) |
| [EPUB](https://en.wikipedia.org/wiki/EPUB)                              | `.epub`         | Yes  | Yes   | [EPUB 3 spec](https://www.w3.org/TR/epub-33/)                                                                      | eBook                                                                            |
| MAFF                                                                    | `.maff`         | Yes  | Yes   | [MAFF spec](http://maf.mozdev.org/maff-specification.html)                                                         | Mozilla Archive Format                                                           |
| [KMZ](https://en.wikipedia.org/wiki/Keyhole_Markup_Language)            | `.kmz`          | Yes  | Yes   | [KML spec](https://www.ogc.org/standards/kml)                                                                      | Google Earth                                                                     |
| NuPkg                                                                   | `.nupkg`        | Yes  | Yes   | [NuGet spec](https://learn.microsoft.com/en-us/nuget/reference/nuspec)                                             | NuGet package                                                                    |
| [DOCX](https://en.wikipedia.org/wiki/Office_Open_XML)                   | `.docx`         | Yes  | Yes   | [ECMA-376](https://www.ecma-international.org/publications-and-standards/standards/ecma-376/)                      | OOXML Word                                                                       |
| XLSX                                                                    | `.xlsx`         | Yes  | Yes   | [ECMA-376](https://www.ecma-international.org/publications-and-standards/standards/ecma-376/)                      | OOXML Excel                                                                      |
| PPTX                                                                    | `.pptx`         | Yes  | Yes   | [ECMA-376](https://www.ecma-international.org/publications-and-standards/standards/ecma-376/)                      | OOXML PowerPoint                                                                 |
| [ODT](https://en.wikipedia.org/wiki/OpenDocument)                       | `.odt`          | Yes  | Yes   | [OASIS ODF](https://www.oasis-open.org/standard/odf/)                                                              | OpenDocument Text                                                                |
| ODS                                                                     | `.ods`          | Yes  | Yes   | [OASIS ODF](https://www.oasis-open.org/standard/odf/)                                                              | OpenDocument Spreadsheet                                                         |
| ODP                                                                     | `.odp`          | Yes  | Yes   | [OASIS ODF](https://www.oasis-open.org/standard/odf/)                                                              | OpenDocument Presentation                                                        |
| CBZ                                                                     | `.cbz`          | Yes  | Yes   | [Comic book archive](https://en.wikipedia.org/wiki/Comic_book_archive)                                             | Comic book ZIP                                                                   |
| CBR                                                                     | `.cbr`          | Yes  | Yes   | [Comic book archive](https://en.wikipedia.org/wiki/Comic_book_archive)                                             | Comic book RAR — delegates to RarWriter                                          |
| XPS / OXPS                                                              | `.xps`,`.oxps`  | Yes  | Yes   | [ECMA-388 OpenXPS](https://ecma-international.org/publications-and-standards/standards/ecma-388/)                  | Microsoft / OpenXPS OPC PDF alternative                                          |
| VSDX                                                                    | `.vsdx`,`.vstx` | Yes  | Yes   | [Visio file formats](https://learn.microsoft.com/en-us/office/client-developer/visio/visio-file-formats)           | Microsoft Visio modern (drawing / template / stencil ± macro)                    |

### OLE2 Compound File variants

Microsoft binary-office formats built on the
[OLE2 / Compound File Binary (CFB)](https://en.wikipedia.org/wiki/Compound_File_Binary_Format)
container. WORM creation produces a structurally-valid CFB envelope (round-trips through our
reader and other permissive CFB tools like libgsf / Apache POI) but is **not** a real
Word/Excel/PowerPoint/Outlook document — those require generating each application's internal
binary stream layout, which is out of scope. Limitations: ~6.8 MB total file size (109 FAT
sectors, no DIFAT chain), single root storage, stream names ≤ 31 UTF-16 chars.

| Format    | Extensions  | Read | Write | Reference                                                                                  | Notes                                                           |
| --------- | ----------- | ---- | ----- | ------------------------------------------------------------------------------------------ | --------------------------------------------------------------- |
| DOC       | `.doc`      | Yes  | Yes   | [MS-DOC](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-doc/)          | Word 97-2003 (CFB envelope, not a real Word document)           |
| XLS       | `.xls`      | Yes  | Yes   | [MS-XLS](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-xls/)          | Excel 97-2003 (CFB envelope, not a real workbook)               |
| PPT       | `.ppt`      | Yes  | Yes   | [MS-PPT](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-ppt/)          | PowerPoint 97-2003 (CFB envelope, not a real presentation)      |
| MSG       | `.msg`      | Yes  | Yes   | [MS-OXMSG](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxmsg/) | Outlook message (CFB envelope, not real MAPI properties)       |
| Thumbs.db | `Thumbs.db` | Yes  | Yes   | [Forensics docs](https://www.forensicswiki.xyz/wiki/Thumbs.db)                             | Windows thumbnail cache (CFB envelope, not real Catalog layout) |
| MSI       | `.msi`      | Yes  | Yes   | [MS-MSI](https://learn.microsoft.com/en-us/windows/win32/msi/windows-installer-file-format) | Windows Installer (CFB envelope, not a functional Installer DB) |

### Compression-stream formats (single-file codecs)

| Format                                                                                  | Extensions          | Compress | Decompress | Reference                                                                                              |
| --------------------------------------------------------------------------------------- | ------------------- | -------- | ---------- | ------------------------------------------------------------------------------------------------------ |
| [Gzip](https://en.wikipedia.org/wiki/Gzip)                                              | `.gz`               | Yes      | Yes        | [RFC 1952](https://www.rfc-editor.org/rfc/rfc1952)                                                     |
| [BZip2](https://en.wikipedia.org/wiki/Bzip2)                                            | `.bz2`              | Yes      | Yes        | [bzip2 source](https://sourceware.org/bzip2/)                                                          |
| [XZ](https://en.wikipedia.org/wiki/XZ_Utils)                                            | `.xz`               | Yes      | Yes        | [XZ format](https://tukaani.org/xz/xz-file-format.txt)                                                 |
| [Zstandard](https://en.wikipedia.org/wiki/Zstd)                                         | `.zst`              | Yes      | Yes        | [RFC 8878](https://www.rfc-editor.org/rfc/rfc8878)                                                     |
| [LZ4](https://en.wikipedia.org/wiki/LZ4_(compression_algorithm))                        | `.lz4`              | Yes      | Yes        | [LZ4 frame format](https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md)                        |
| [Brotli](https://en.wikipedia.org/wiki/Brotli)                                          | `.br`               | Yes      | Yes        | [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932)                                                     |
| [Snappy](https://en.wikipedia.org/wiki/Snappy_(compression))                            | `.sz`,`.snappy`     | Yes      | Yes        | [Snappy framing](https://github.com/google/snappy/blob/main/framing_format.txt)                        |
| [LZOP](https://en.wikipedia.org/wiki/Lzop)                                              | `.lzo`              | Yes      | Yes        | [lzop source](https://www.lzop.org/)                                                                   |
| [compress (.Z)](https://en.wikipedia.org/wiki/Compress_(software))                      | `.Z`                | Yes      | Yes        | [ncompress](https://github.com/vapier/ncompress)                                                       |
| [LZMA](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Markov_chain_algorithm) | `.lzma`             | Yes      | Yes        | [7-Zip LZMA SDK](https://www.7-zip.org/sdk.html)                                                       |
| [Lzip](https://en.wikipedia.org/wiki/Lzip)                                              | `.lz`               | Yes      | Yes        | [lzip format](https://www.nongnu.org/lzip/manual/lzip_manual.html)                                     |
| [Zlib](https://en.wikipedia.org/wiki/Zlib)                                              | `.zlib`             | Yes      | Yes        | [RFC 1950](https://www.rfc-editor.org/rfc/rfc1950)                                                     |
| SZDD                                                                                    | `.sz_`              | Yes      | Yes        | [compress.exe format](https://www.stdlib.at/)                                                          |
| KWAJ                                                                                    | -                   | Yes      | Yes        | [MS compress formats](http://fileformats.archiveteam.org/wiki/KWAJ)                                    |
| [RZIP](https://en.wikipedia.org/wiki/Rzip)                                              | `.rz`               | Yes      | Yes        | [rzip docs](http://rzip.samba.org/)                                                                    |
| [MacBinary](https://en.wikipedia.org/wiki/MacBinary)                                    | `.bin`              | Yes      | Yes        | [RFC 1740](https://www.rfc-editor.org/rfc/rfc1740)                                                     |
| [BinHex](https://en.wikipedia.org/wiki/BinHex)                                          | `.hqx`              | Yes      | Yes        | [RFC 1741](https://www.rfc-editor.org/rfc/rfc1741)                                                     |
| [Squeeze](https://en.wikipedia.org/wiki/Squeeze_(file_format))                          | `.sqz`              | Yes      | Yes        | [Squeeze format](http://fileformats.archiveteam.org/wiki/SQ)                                           |
| PowerPacker                                                                             | `.pp`               | Yes      | Yes        | [Amiga PP20](http://fileformats.archiveteam.org/wiki/Powerpacker)                                      |
| ICE Packer                                                                              | `.ice`              | Yes      | Yes        | [Atari ST ICE](http://fileformats.archiveteam.org/wiki/ICE)                                            |
| [PackBits](https://en.wikipedia.org/wiki/PackBits)                                      | `.packbits`         | Yes      | Yes        | [Apple PackBits](https://en.wikipedia.org/wiki/PackBits)                                               |
| Yaz0 (SZS)                                                                              | `.yaz0`,`.szs`      | Yes      | Yes        | [Nintendo Yaz0 RE](https://wiki.tockdom.com/wiki/YAZ0)                                                 |
| BriefLZ                                                                                 | `.blz`              | Yes      | Yes        | [BriefLZ source](https://github.com/jibsen/brieflz)                                                    |
| RNC                                                                                     | `.rnc`              | Yes      | Yes        | [Rob Northen RE](http://segaretro.org/Rob_Northen_compression)                                         |
| RefPack / QFS                                                                           | `.qfs`,`.refpack`   | Yes      | Yes        | [RefPack RE](http://wiki.niotso.org/RefPack)                                                           |
| aPLib                                                                                   | `.aplib`            | Yes      | Yes        | [aPLib docs](http://ibsensoftware.com/products_aPLib.html)                                             |
| [LZFSE](https://en.wikipedia.org/wiki/LZFSE)                                            | `.lzfse`            | Yes      | Yes        | [Apple LZFSE source](https://github.com/lzfse/lzfse)                                                   |
| Freeze                                                                                  | `.f`,`.freeze`      | Yes      | Yes        | [Unix Freeze](http://fileformats.archiveteam.org/wiki/Freeze)                                          |
| [uuencoding](https://en.wikipedia.org/wiki/Uuencoding)                                  | `.uu`,`.uue`        | Yes      | Yes        | [POSIX uuencode](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/uuencode.html)             |
| [yEnc](https://en.wikipedia.org/wiki/YEnc)                                              | `.yenc`             | Yes      | Yes        | [yEnc spec](http://www.yenc.org/yenc-draft.1.3.txt)                                                    |
| Density                                                                                 | `.density`          | Yes      | Yes        | [Density source](https://github.com/k0dai/density)                                                     |
| LZG                                                                                     | `.lzg`              | Yes      | Yes        | [LZG source](https://github.com/mbitsnbites/liblzg)                                                    |
| BCM                                                                                     | `.bcm`              | Yes      | Yes        | [BCM source](https://github.com/encode84/bcm)                                                          |
| BSC                                                                                     | `.bsc`              | Yes      | Yes        | [libbsc](https://github.com/IlyaGrebnov/libbsc)                                                        |
| BALZ                                                                                    | `.balz`             | Yes      | Yes        | [BALZ source](https://sourceforge.net/projects/balz/)                                                  |
| CSC                                                                                     | `.csc`              | Yes      | Yes        | [CSC source](https://github.com/fusiyuan2010/CSC)                                                      |
| Zling                                                                                   | `.zling`            | Yes      | Yes        | [libzling](https://github.com/richox/libzling)                                                         |
| [Lizard](https://github.com/inikep/lizard)                                              | `.lizard`           | Yes      | Yes        | [Lizard source](https://github.com/inikep/lizard)                                                      |
| QuickLZ                                                                                 | `.quicklz`          | Yes      | Yes        | [QuickLZ docs](http://www.quicklz.com/)                                                                |
| [cmix](https://www.byronknoll.com/cmix.html)                                            | `.cmix`             | Yes      | Yes        | [cmix source](https://github.com/byronknoll/cmix)                                                      |
| MCM                                                                                     | `.mcm`              | Yes      | Yes        | [MCM source](https://github.com/mathieuchartier/mcm)                                                   |
| [PAQ8](https://en.wikipedia.org/wiki/PAQ)                                               | `.paq8`             | Yes      | Yes        | [Matt Mahoney PAQ page](https://mattmahoney.net/dc/)                                                   |
| [SWF](https://en.wikipedia.org/wiki/SWF)                                                | `.swf`              | Yes      | Yes        | [SWF 19 spec](https://open-flash.github.io/mirrors/swf-spec-19.pdf)                                    |
| CP/M Crunch                                                                             | `.cru`              | Yes      | Yes        | [CP/M CRUNCH](http://www.retroarchive.org/docs/cpm.html)                                               |
| [PPMd](https://en.wikipedia.org/wiki/Prediction_by_partial_matching)                    | `.pmd`              | Yes      | Yes        | [Shkarin PPMd](https://github.com/jk-jeon/PPMd)                                                        |
| LZHAM                                                                                   | `.lzham`            | Yes      | Yes        | [LZHAM source](https://github.com/richgel999/lzham_codec)                                              |
| LZS                                                                                     | `.lzs`              | Yes      | Yes        | [RFC 1967](https://www.rfc-editor.org/rfc/rfc1967) / [RFC 2395](https://www.rfc-editor.org/rfc/rfc2395)|

### Compound formats

`tar.gz`, `tar.bz2`, `tar.xz`, `tar.zst`, `tar.lz4`, `tar.lz`, `tar.br` — auto-detected on
read; matching writer composes the inner TAR with the outer compression stream.

### Pseudo-archive containers

Formats whose binary structure already addresses N independent payloads, even though they're
not traditionally seen as archives. Each surfaces an `IArchiveFormatOperations.List` with one
entry per inner payload (resource / icon / font / glyph / message / segment / track …).

| Container                 | State | Description                                                                            |
| ------------------------- | ----- | -------------------------------------------------------------------------------------- |
| `FileFormat.PeResources`  | R     | PE/COFF `.rsrc` directory — one entry per `RT_*` resource, `.ico`/`.bmp`/`.xml`/`.txt` |
| `FileFormat.ResourceDll`  | WORM  | Pure-resource DLL — same surface as `PeResources` but can also synthesise a fresh DLL  |
| `FileFormat.ExePackers`   | R     | Demoscene + classic DOS / PE packer detection (PKLITE / LZEXE / Petite / FSG / MEW / MPRESS / Crinkler / kkrunchy / ASPack / NsPack / Yoda / ASProtect / Themida / VMProtect …) — surfaces a `metadata.ini` evidence record |
| `FileFormat.Ico`          | WORM  | ICO / CUR — one `.png` / `.bmp` per `ICONDIRENTRY`                                     |
| `FileFormat.Ani`          | R     | Animated cursor — one `.cur` per frame                                                 |
| `FileFormat.FontCollection` | R   | TTC / OTC — one `.ttf` / `.otf` per member font; per-glyph `.svg` for single fonts     |
| `FileFormat.Gettext`      | R     | gettext `.mo` / `.po` — one `.txt` per `msgid` / `msgstr` pair                         |

### Streaming / video / subtitle containers

Multi-track / multi-segment containers from the streaming and broadcast world. Each demuxes
into per-track or per-segment archive entries.

| Container             | State | Description                                                                            |
| --------------------- | ----- | -------------------------------------------------------------------------------------- |
| `FileFormat.Mp4`      | R     | MP4 / MOV / Apple QuickTime — atom walker; tracks → H.264 Annex-B + AAC ADTS           |
| `FileFormat.Matroska` | R     | MKV / WebM — EBML walker; tracks + attachments + chapters                              |
| `FileFormat.Avi`      | R     | AVI RIFF — `LIST/movi` per-stream demuxer                                              |
| `FileFormat.MpegTs`   | R     | MPEG-2 Transport Stream (`.ts` / `.m2ts` / `.mts`) — per-PID elementary streams        |
| `FileFormat.Sup`      | R     | Blu-ray PGS subtitle — segments grouped by epoch                                       |
| `FileFormat.VobSub`   | R     | DVD VobSub `.idx + .sub` pair — per-entry slices of the sibling `.sub` PES stream      |
| `FileFormat.M3u8`     | R     | HLS playlist — one entry per segment + per-variant metadata                            |

### Scientific / ML data containers

| Container          | State | Reference                                                                                          | Notes                                                                                                                  |
| ------------------ | ----- | -------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| `FileFormat.Numpy` | R     | [NEP 1 / npy-format](https://numpy.org/neps/nep-0001-npy-format.html)                              | NumPy `.npy` (single ndarray) and `.npz` (ZIP of NPYs); header parser surfaces shape / dtype / fortran-order metadata |
| `FileFormat.Hdf4`  | R     | [HDF4 reference](https://support.hdfgroup.org/release4/doc/)                                       | HDF4 DD linked-list walker; per-DD entry with tag histogram                                                            |
| `FileFormat.Hdf5`  | R     | [HDF5 file format spec](https://docs.hdfgroup.org/hdf5/v1_14/_f_m_t3.html)                         | HDF5 superblock + group walk; pseudo-archive surface for now                                                           |
| `FileFormat.Nifti` | R     | [NIfTI spec](https://nifti.nimh.nih.gov/nifti-1/documentation)                                     | Medical imaging (MRI); 352-byte v1 / 540-byte v2 header + voxel data; transparent gzip                                 |
| `FileFormat.Onnx`  | R     | [ONNX proto](https://github.com/onnx/onnx/blob/main/onnx/onnx.proto)                               | Pure-C# protobuf reader; surfaces graph initializers as entries                                                        |
| `FileFormat.Dicom` | R     | [NEMA DICOM PS3](https://www.dicomstandard.org/current)                                            | Medical imaging (CT / MRI / X-ray); single DICOM image + DICOMDIR multi-study patient/series index                     |

### CAD / 3D scene formats

| Container             | State | Reference                                                                                                              | Notes                                                                                |
| --------------------- | ----- | ---------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------ |
| `FileFormat.Stl`      | R     | [STL spec](http://www.ennex.com/~fabbers/StL.asp)                                                                      | ASCII + binary; triangle count, bounding box, name                                   |
| `FileFormat.Ply`      | R     | [Stanford PLY](http://paulbourke.net/dataformats/ply/)                                                                 | ASCII / binary LE/BE, element schema                                                 |
| `FileFormat.Dxf`      | R     | [Autodesk DXF ref](https://help.autodesk.com/view/OARX/2022/ENU/?guid=GUID-235B22E0-A567-4CF6-92D3-38A2306D73F3)       | AutoCAD ASCII; section list + entity histogram                                       |
| `FileFormat.Collada`  | R     | [Khronos Collada 1.5](https://www.khronos.org/files/collada_spec_1_5.pdf)                                              | XML 3D interchange                                                                   |
| `FileFormat.Obj`      | R     | [Wavefront OBJ](https://en.wikipedia.org/wiki/Wavefront_.obj_file)                                                     | Wavefront mesh; ASCII triangles + materials                                          |

### Executable packer detection

`FileFormat.ExePackers` is a pseudo-archive that detects-and-surfaces evidence about
executable packers / PE protectors. Each detected packer produces a `metadata.ini` (signature
offset, version byte, packer-header fields) plus a `packed_payload.bin` (or in-process
decompressed body for UPX). The detector handles: PKLITE / LZEXE / Petite / Shrinkler / FSG /
MEW / MPRESS / Crinkler / kkrunchy / ASPack / NsPack / Yoda's Crypter / ASProtect / Themida /
VMProtect.

UPX gets its own descriptor (`FileFormat.Upx`) with a hardened detection pipeline (BSS-style
first-section + RWX flags + entry-in-last-section + payload-entropy fingerprint that catches
binaries with the `UPX!` magic wiped) and an in-process decompressor for NRV2B / NRV2D /
NRV2E (LE32 + LE16 + LE8 variants) and LZMA payloads via the `BB_Nrv2{b,d,e}` and `BB_Lzma`
building blocks. PE header reconstruction (IAT / OEP) is delegated to the original `upx -d`.

### Backup-software disk images

Whole-system / partition backups from consumer + enterprise backup suites. Closed-source
formats were elevated from Stage-0 detection-only via direct binary reverse engineering of
publicly-distributed vendor binaries; each descriptor's `Description` field names exactly
which fields are decoded against the binary versus documented-TODO. R/W = our writer +
reader round-trip; WORM = our writer emits a fresh image our reader walks back, but vendor
byte-compat stays explicitly out of scope (no real vendor samples available for clean-room
validation, or vendor tooling is restore-only).

| Container                | State | Reference                                                                                                    | Notes                                                                                                                                                                                       |
| ------------------------ | ----- | ------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `FileFormat.Bkf`         | R     | [MS-MTF](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-mtf/)                              | NTBackup MTF Tape Format; DBLK-chain walker, FILE + DATA entry types, `TAPE` magic at offset 0                                                                                              |
| `FileFormat.AppleSparse` | R     | [hdiutil(1)](https://www.unix.com/man-page/osx/1/hdiutil/) + Time Machine band layout                        | sparseimage band-allocation table + sparsebundle band fan-out; inner HFS+ / APFS delegated                                                                                                  |
| `FileFormat.Partclone`   | R     | [Clonezilla partclone source](https://github.com/Tomas-M/partclone)                                          | `image_head` + `fs_info` + bitmap → sector reconstruction; per-FS family backed by the matching FS reader                                                                                   |
| `FileFormat.Ghost`       | R/W   | Reverse-engineered from Symantec Ghost Explorer 2003                                                         | Modern 3.0 → 11.x with Fast LZ Z1 + zlib Z3-Z9 codecs + CRC-16 stream cipher; legacy DOS-era Ghost 1.x / 2.x stays Stage-0 with version-gated diagnostic fallback                            |
| `FileFormat.Macrium`     | R/W   | [Macrium mrimgx file layout](https://github.com/macrium/mrimgx_file_layout) (MIT)                            | Reflect X `.mrimgx` per the open spec — AES-CBC + PBKDF2-HMAC-SHA256 600k-iter + ESSIV-style per-block IV + zstd                                                                            |
| `FileFormat.Acronis`     | R     | Reverse-engineered from `ti_tools.dll` (ATI 2018 32-bit) and dennisss's prior framing                        | Classic `.tib` — Listing → RecordIndex chain walk via `MetaOffset` anchors; FileMeta 102/1/2/5 body decoded as InputItem attribute stream; ItemCommon 0x10 surfaces filenames + altnames     |
| `FileFormat.AcronisTibx` | R     | Reverse-engineered from `libarchive3.so` (ATI 2021 Linux ELF) + `archive3.dll` (ATI 2018 Windows)            | Modern `.tibx` — 4096-byte page-zero header parser; `"ARCH"` magic; LSM tree walk + page-type table; LSM file-listing extraction is documented-TODO past Stage-1 metadata                  |
| `FileFormat.Aomei`       | WORM  | Reverse-engineered from AOMEI Backupper Standard binaries (Binary Research `d:\work\br\src\imgfile`)         | `.adi` / `.afi` via BIFH (0x65C) head + BIFT (0x674) tail + BR\_STANDARD\_HEADER tagged-record framing; INFO\_TYPE\_COMPRESS / ENCRYPT / PASSWORD / BACKUP\_TYPE pinned; INDEX\_TYPE\_DATABLOCK body documented-TODO |
| `FileFormat.Paragon`     | WORM  | Reverse-engineered from Paragon Hard Disk Manager 18                                                         | `.pbf` — vendor-literal `PImg` magic + Major 0x0002 / FormatVersion 0x0003 prefix; CWBP write-once chunk-offset table + per-chunk zlib + Adler-32; HDM 16+ is restore-only so no byte-compat |
| `FileFormat.Veeam`       | R     | [Synacktiv Velociraptor artifact](https://github.com/synacktiv/veeam-velociraptor)                           | `.vbk` / `.vib` / `.vrb` — Stage-1 trailing `<OibSummary>` plaintext XML island only; chunked compressed block layer has no published spec (CBT chain + dedup pool + AES-256 gated)         |
| `FileFormat.EaseUs`      | R     | Reverse-engineered from EaseUS Todo Backup + Rune-Server thread 694189                                       | `.pbd` — `IMGF` / `FIMG` magic; zlib chunk stream extracted via linear-scan + trial-inflate; full container chain replay is documented-TODO                                                 |

### Known limitations

Code paths that throw `NotSupportedException` or `NotImplementedException` rather than
silently producing wrong output:

| Area                              | State                                                                                                                                                                                                                      |
| --------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| LZFSE V1 / V2 blocks              | FSE/tANS backend not implemented — uncompressed (`bvxn`) + LZVN blocks work. Full LZFSE needs ~1500 LOC new code                                                                                                          |
| ZPAQ                              | Reader requires a ZPAQL virtual machine (not implemented). Multi-week bytecode-VM project                                                                                                                                  |
| StuffIt X writer                  | Proprietary element-catalog / P2-varint writer not implemented — WORM emits valid `StuffIt!` envelope shell. No public spec                                                                                               |
| UMX writer                        | Full export table + compact-index music encoding not implemented — WORM emits valid header only                                                                                                                            |
| OLE2 application streams (DOC/XLS/PPT/MSG/ThumbsDb/MSI) | CFB envelope round-trips through our reader and libgsf/Apache POI, but the internal `WordDocument` / `WorkBook` / `PowerPoint Document` / MAPI / Catalog / Installer-DB streams are not synthesised |
| Inno Setup reader                 | Individual file extraction from `Setup.1` not implemented for some installer versions                                                                                                                                      |
| RAR create                        | Only v4 and v5 archive creation are implemented                                                                                                                                                                            |

### Modern packaging (read-only)

| Format                                                          | Extensions              | Read | Write | Reference                                                                                                                          | Notes                                                                                |
| --------------------------------------------------------------- | ----------------------- | ---- | ----- | ---------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------ |
| [AppImage](https://en.wikipedia.org/wiki/AppImage)              | `.AppImage`             | Yes  | -     | [AppImage spec](https://github.com/AppImage/AppImageSpec)                                                                          | ELF stub + appended SquashFS; offset located by ELF section-end + magic scan         |
| [Snap](https://en.wikipedia.org/wiki/Snap_(software))           | `.snap`                 | Yes  | -     | [snapd source](https://github.com/snapcore/snapd)                                                                                  | SquashFS with `meta/snap.yaml`                                                       |
| [MSIX](https://en.wikipedia.org/wiki/MSIX)                      | `.msix`,`.msixbundle`   | Yes  | Yes   | [MSIX spec](https://learn.microsoft.com/en-us/windows/msix/)                                                                       | Modern Windows app package (mirrors APPX); WORM emits unsigned bundle               |
| ESD                                                             | `.esd`                  | Yes  | -     | [WIM/ESD overview](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wim-and-esd-windows-image-files-overview) | Windows Update encrypted-LZMS WIM; shares `MSWIM\0\0\0` magic, extension-only        |
| Split WIM                                                       | `.swm`,`.swmN`          | Yes  | -     | [WIM spec](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/)                                                | Multi-part WIM volume                                                                |
| [WACZ](https://specs.webrecorder.net/wacz/1.0.0/)               | `.wacz`                 | Yes  | -     | [WACZ 1.0.0](https://specs.webrecorder.net/wacz/1.0.0/)                                                                            | Web Archive Collection Zipped — ZIP around WARC + `datapackage.json`                 |
| [Python Wheel](https://en.wikipedia.org/wiki/Wheel_(software))  | `.whl`                  | Yes  | -     | [PEP 427](https://peps.python.org/pep-0427/)                                                                                       | ZIP with `dist-info/METADATA`, `WHEEL`, `RECORD`                                     |
| [Ruby Gem](https://en.wikipedia.org/wiki/RubyGems)              | `.gem`                  | Yes  | -     | [gem spec](https://guides.rubygems.org/specification-reference/)                                                                   | TAR with `metadata.gz`, `data.tar.gz`, `checksums.yaml.gz`                           |
| Rust Crate                                                      | `.crate`                | Yes  | -     | [cargo spec](https://doc.rust-lang.org/cargo/reference/registries.html)                                                            | TAR.GZ with single `name-version/` directory containing `Cargo.toml`                 |

## Versioning

Version-locked 1:1 with `Hawkynt.Compression.Core`. Pin both at the same version.

## License

LGPL-3.0-or-later. See the source repository for the full license text.
