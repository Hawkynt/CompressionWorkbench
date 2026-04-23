# CompressionWorkbench

[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
[![Release](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/release.yml/badge.svg)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/Hawkynt/CompressionWorkbench?label=release&sort=semver)](https://github.com/Hawkynt/CompressionWorkbench/releases/latest)
[![Latest nightly](https://img.shields.io/github/v/release/Hawkynt/CompressionWorkbench?include_prereleases&label=nightly&sort=date)](https://github.com/Hawkynt/CompressionWorkbench/releases?q=prerelease%3Atrue)
![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)
![Language](https://img.shields.io/github/languages/top/Hawkynt/CompressionWorkbench?color=purple)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/CompressionWorkbench?branch=main)![Activity](https://img.shields.io/github/commit-activity/y/Hawkynt/CompressionWorkbench?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/commits/main)
[![Downloads](https://img.shields.io/github/downloads/Hawkynt/CompressionWorkbench/total)](https://github.com/Hawkynt/CompressionWorkbench/releases)

> A fully clean-room C# implementation of compression primitives, archive file formats, and analysis tools. Every algorithm is implemented from scratch using no external compression source code - only our own primitives.

---

## Vision

CompressionWorkbench is both a **toolkit** and a **learning platform** for data compression. It aims to:

1. **Implement every major compression algorithm from scratch** - no wrappers around zlib, liblzma, or other native libraries
2. **Support every common archive format** - read, write, convert, and optimize across different-era formats
3. **Treat every multi-payload container as an archive** - PE resources, multi-page TIFF, multi-frame GIF, font collections, iWork packages, anything with N addressable entries gets the same List/Extract/Create interface as ZIP (see [docs/multi-payload-archives.md](docs/multi-payload-archives.md))
4. **Provide a binary analysis engine** - identify unknown compressed data, map entropy, fingerprint algorithms, and reconstruct compression chains
5. **Benchmark compression building blocks** - compare raw algorithm performance (ratio, speed) across data patterns
6. **Offer multiple interfaces** - GUI, CLI tool, Explorer shell integration, and self-extracting archives

---

## Domains

| Domain           | Scope                                                                                                                             |
| ---------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| **Primitives**   | Bit I/O, Huffman, arithmetic/range coding, ANS/FSE, LZ family, BWT, MTF, PPM, context mixing, DPCM, DMC, Tunstall                |
| **Formats**      | 178 format descriptors: archive containers, compression streams, filesystem images, game archives, virtual disks, retro computing |
| **Analysis**     | Signature scanning, algorithm fingerprinting, entropy mapping, trial decompression, chain reconstruction, black-box tool integration, streaming analysis |
| **Benchmarking** | Compare 49 building blocks (raw algorithms without file format overhead) across data patterns                                      |
| **Tools**        | CLI archiver (`cwb`), WPF browser, Explorer shell integration, SFX stubs                                                          |

---

## Technology Stack

| Concern   | Choice                                                              |
| --------- | ------------------------------------------------------------------- |
| Language  | C# 14 / .NET 10                                                     |
| Solution  | `.slnx` (XML solution format)                                       |
| Testing   | NUnit                                                               |
| GUI       | WPF (`Compression.UI`)                                              |
| Shell     | Explorer context menu (`Compression.Shell`)                         |
| CLI       | System.CommandLine v3 (`Compression.CLI`)                           |
| Analysis  | `Compression.Analysis` library                                      |
| SFX       | `Compression.Sfx.Cli` / `Compression.Sfx.Ui` stubs                  |
| Discovery | Roslyn source generator (zero-reflection format/block registration) |

---

## Solution Structure

```
CompressionWorkbench.slnx
|
+-- Compression.Core              Core primitives, building blocks, and algorithms
+-- Compression.Registry          Interfaces (IFormatDescriptor, IBuildingBlock), registries
+-- Compression.Registry.Generator  Roslyn source generator for auto-discovery
+-- Compression.Lib               Umbrella library: format detection, archive operations, SFX
+-- Compression.Analysis          Binary analysis engine (signatures, entropy, fingerprinting)
+-- Compression.CLI               Universal command-line archive tool (cwb)
+-- Compression.UI                WPF archive browser & analysis wizard
+-- Compression.Shell             Explorer context menu integration
+-- Compression.Sfx.Cli           Self-extracting archive stub (console)
+-- Compression.Sfx.Ui            Self-extracting archive stub (GUI)
+-- Compression.Tests             NUnit test project
|
+-- FileFormat.Ace                ACE archive (v1/v2, sound/picture filters, Blowfish)
+-- FileSystem.Adf                Amiga Disk File (OFS/FFS, r/w)
+-- FileFormat.AlZip              ALZip (.alz) Korean archive (Deflate)
+-- FileFormat.Ampk               Amiga AMPK archive
+-- FileFormat.ApLib               aPLib compression stream
+-- FileSystem.Apfs               Apple File System (NXSB, r/o)
+-- FileFormat.Ar                 Unix AR archive
+-- FileFormat.Arc                ARC archive (methods 0-9)
+-- FileFormat.Arj                ARJ archive (methods 0-4, garble)
+-- FileFormat.Balz               BALZ (ROLZ + arithmetic) stream
+-- FileFormat.Bcm                BCM (BWT + arithmetic) stream
+-- FileFormat.Big                EA Games BIG archive (BIGF/BIG4)
+-- FileFormat.BinCue             BIN/CUE disc image
+-- FileFormat.BinHex             BinHex (.hqx) stream
+-- FileFormat.BriefLz            BriefLZ compression stream
+-- FileFormat.Brotli             Brotli compression stream
+-- FileFormat.Bsc                BSC (block-sorting compressor) stream
+-- FileFormat.Bsa                Bethesda BSA/BA2 game archive
+-- FileSystem.Btrfs              Btrfs filesystem (r/o)
+-- FileFormat.Bzip2              BZip2 compression stream
+-- FileFormat.Cab                Microsoft Cabinet (MSZIP/LZX/Quantum)
+-- FileFormat.Cdi                DiscJuggler disc image
+-- FileFormat.Chm                Microsoft Compiled HTML Help (r/o, LZX)
+-- FileFormat.Cmix               cmix (neural network) stream
+-- FileFormat.Compress           Unix .Z (LZW) stream
+-- FileFormat.CompactPro         Classic Mac Compact Pro archive
+-- FileSystem.CpcDsk             Amstrad CPC disk image (r/w)
+-- FileFormat.Cpio               CPIO archive (bin/odc/newc/CRC)
+-- FileSystem.CramFs             CramFS filesystem image
+-- FileFormat.Crunch             CP/M Crunch (LZW) stream
+-- FileFormat.Csc                CSC (context-based) stream
+-- FileSystem.D64                C64 1541 disk image (r/w)
+-- FileSystem.D71                C128 1571 disk image (r/w)
+-- FileSystem.D81                C128 1581 disk image (r/w)
+-- FileFormat.Deb                Debian package archive
+-- FileFormat.Density            Density (Centaurean) stream
+-- FileFormat.DiskDoubler        DiskDoubler archive
+-- FileFormat.Dmg                Apple DMG disk image (r/o, zlib)
+-- FileFormat.Dms                Amiga DMS archive
+-- FileSystem.DoubleSpace        DoubleSpace/DriveSpace CVF (r/w)
+-- FileSystem.ExFat              exFAT filesystem (r/w)
+-- FileSystem.Ext                ext2/3/4 filesystem (r/w)
+-- FileSystem.F2fs               F2FS filesystem (r/o)
+-- FileSystem.Fat                FAT12/16/32 filesystem (r/w)
+-- FileFormat.Flac               FLAC lossless audio compression stream
+-- FileFormat.FreeArc            FreeArc archive (r/w, store mode)
+-- FileFormat.Freeze             Freeze (Unix) stream
+-- FileFormat.GodotPck           Godot Engine PCK resource pack (r/w)
+-- FileFormat.Grp                BUILD Engine GRP archive (r/w)
+-- FileFormat.Gzip               Gzip compression stream
+-- FileFormat.Ha                 HA archive (HSC/ASC)
+-- FileSystem.Hfs                HFS classic filesystem (r/w)
+-- FileSystem.HfsPlus            HFS+ filesystem (r/w)
+-- FileFormat.Hog                Descent HOG archive (r/w)
+-- FileFormat.IcePacker          Atari ST ICE Packer stream
+-- FileFormat.IffCdaf            IFF-CDAF archive
+-- FileFormat.InnoSetup          Inno Setup installer (r/o)
+-- FileSystem.Iso                ISO 9660 filesystem (r/w)
+-- FileSystem.Jfs                JFS filesystem (r/o)
+-- FileFormat.Kwaj               MS-DOS COMPRESS.EXE extended stream
+-- FileFormat.Lbr                CP/M LBR archive
+-- FileFormat.LhF                LhF archive
+-- FileFormat.Lizard             Lizard (LZ5) stream
+-- FileFormat.Lz4                LZ4 block + frame stream
+-- FileFormat.Lzfse              Apple LZFSE/LZVN stream
+-- FileFormat.Lzg                LZG compression stream
+-- FileFormat.Lzh                LZH/LHA archive (lh0-lh7, lzs, pm0-pm2)
+-- FileFormat.Lzham              LZHAM compression stream
+-- FileFormat.Lzip               Lzip compression stream
+-- FileFormat.Lzma               LZMA alone stream
+-- FileFormat.Lzop               LZOP compression stream
+-- FileFormat.Lzs                LZS (Stac/Cisco, RFC 1967) compression stream
+-- FileFormat.Lzx                Amiga LZX archive
+-- FileFormat.MacBinary          MacBinary stream
+-- FileFormat.Mcm                MCM (context mixing) stream
+-- FileFormat.Mdf                Alcohol 120% MDF disc image
+-- FileSystem.Mfs                Macintosh File System (r/w)
+-- FileSystem.MinixFs            Minix filesystem v1/v2/v3 (r/w)
+-- FileFormat.Mpq                Blizzard MPQ game archive
+-- FileSystem.Msa                Atari ST MSA disk image
+-- FileFormat.Msi                MSI (OLE Compound File) archive
+-- FileFormat.Nds                Nintendo DS ROM archive
+-- FileFormat.Nrg                Nero disc image
+-- FileFormat.Nsa                NScripter NSA archive
+-- FileFormat.Nsis               NSIS installer (r/o)
+-- FileSystem.Ntfs               NTFS filesystem (r/w, LZNT1)
+-- FileFormat.PackBits           PackBits RLE stream
+-- FileFormat.PackDisk           Amiga PackDisk archive
+-- FileFormat.PackIt             PackIt archive
+-- FileFormat.Pak                PAK archive (ARC-compatible)
+-- FileFormat.Paq8               PAQ8 (context mixing) stream
+-- FileFormat.Pdf                PDF image extraction
+-- FileFormat.Ppmd               PPMd standalone compression stream
+-- FileFormat.PowerPacker        Amiga PowerPacker (PP20) stream
+-- FileFormat.Qcow2              QEMU QCOW2 disk image (r/o, zlib)
+-- FileFormat.QuickLz            QuickLZ compression stream
+-- FileFormat.Rar                RAR archive (v1-v5, read + v4/v5 write)
+-- FileFormat.RefPack            RefPack/QFS (EA) stream
+-- FileSystem.ReiserFs           ReiserFS filesystem (r/o)
+-- FileFormat.Rnc                Rob Northen Compression stream
+-- FileSystem.RomFs              Linux ROMFS filesystem (r/w)
+-- FileFormat.Rpm                RPM package archive
+-- FileFormat.Rzip               RZIP compression stream
+-- FileFormat.Sar                NScripter SAR archive
+-- FileFormat.SevenZip           7z archive (LZMA/LZMA2, BCJ, AES)
+-- FileFormat.Shar               Shell archive
+-- FileFormat.Snappy             Snappy compression stream
+-- FileFormat.SplitFile           Split file (.001) joiner/splitter
+-- FileFormat.Spark              RISC OS Spark archive
+-- FileSystem.SquashFs            SquashFS filesystem image
+-- FileFormat.Sqx                SQX archive (LZH/multimedia/audio)
+-- FileFormat.Squeeze            Squeeze (.sqz) stream
+-- FileFormat.StuffIt            StuffIt archive
+-- FileFormat.StuffItX           StuffIt X archive
+-- FileFormat.Swf                SWF (Flash) stream
+-- FileFormat.Szdd               MS-DOS COMPRESS.EXE LZSS stream
+-- FileFormat.T64                C64 tape image (r/w)
+-- FileFormat.Tap                ZX Spectrum tape image (r/w)
+-- FileFormat.Tar                TAR container (POSIX/GNU/PAX)
+-- FileFormat.Tnef               TNEF (winmail.dat) archive
+-- FileSystem.TrDos              TR-DOS disk image
+-- FileSystem.Udf                UDF filesystem (r/o)
+-- FileSystem.Ufs                UFS filesystem (r/w)
+-- FileFormat.Uharc              UHARC archive
+-- FileFormat.Umx                Unreal UMX package
+-- FileFormat.UuEncoding         UuEncoding stream
+-- FileSystem.Vdfs               VDFS filesystem (r/w)
+-- FileFormat.Vdi                VirtualBox VDI disk image (r/w)
+-- FileFormat.Vhd                VHD disk image (r/w)
+-- FileFormat.Vmdk               VMDK disk image (r/w)
+-- FileFormat.Vpk                Valve VPK game archive
+-- FileFormat.Wad                Doom WAD archive
+-- FileFormat.Wad2               Quake/Half-Life WAD2/WAD3 texture archive
+-- FileFormat.Warc               WARC web archive
+-- FileFormat.Wim                WIM image (LZX/XPRESS)
+-- FileFormat.Wrapster           Wrapster archive
+-- FileFormat.Xar                XAR (eXtensible ARchive)
+-- FileSystem.Xfs                XFS filesystem (r/o)
+-- FileFormat.Xz                 XZ compression stream
+-- FileFormat.Yaz0               Nintendo Yaz0 (SZS) stream
+-- FileFormat.YEnc               yEnc binary-to-text stream
+-- FileFormat.Zap                ZAP archive
+-- FileSystem.Zfs                ZFS filesystem (r/o)
+-- FileFormat.Zip                ZIP archive (Store-Zstd, AES)
+-- FileFormat.Zlib               Zlib (RFC 1950) stream
+-- FileFormat.Zling              Zling (ROLZ + Huffman) stream
+-- FileFormat.Zoo                ZOO archive
+-- FileFormat.Zpaq               ZPAQ archive (context mixing)
+-- FileFormat.Zstd               Zstandard compression stream
```

---

## Compression.Core - Primitives & Building Blocks

Every `FileFormat.*` project builds on these clean-room primitives:

- **Bit I/O**: `BitReader`, `BitWriter` (LSB/MSB-first), `BitBuffer`
- **Huffman Coding**: Static/dynamic trees, canonical codes
- **Arithmetic/Range Coding**: Range coder, binary arithmetic coder, adaptive models
- **ANS/FSE**: Asymmetric Numeral Systems, Finite State Entropy (Zstandard)
- **LZ Family**: LZ77, LZSS, LZ78, LZW, LZMA/LZMA2, LZO (LZO1X/LZO1X-999), LZ4 (fast + HC), LZX, XPRESS, LZMS, Quantum
- **Match Finders**: Hash chain, binary tree, suffix array, rolling hash
- **Transforms**: BWT, MTF, Delta, BCJ (x86/ARM/Thumb/PPC/SPARC/IA64), BCJ2
- **Prediction**: PPMd (Model H/I), context mixing (PAQ-style), order-N models
- **Checksums**: CRC-16, CRC-32, CRC-32C, Adler-32, xxHash32, BLAKE2b, SHA-1, SHA-256, MD5
- **Encryption**: AES-256-CBC/CTR, Blowfish CBC, PBKDF2, ZipCrypto
- **Codecs**: RLE, Golomb-Rice, Exp-Golomb, Elias Gamma/Delta, Levenshtein, Unary, Tunstall, ACE LZ77+Huffman, SQX LZH, Brotli LZ77
- **Data Structures**: Sliding window, priority queue, trie, suffix tree
- **SIMD**: CRC32 hardware acceleration (SSE4.2/ARM), vectorized match finding, SIMD histogram, vectorized RLE scan
- **Memory**: ArrayPool integration in LZ4, Snappy, DEFLATE, Brotli; stackalloc for small buffers

### Building Blocks

Building blocks are raw algorithm primitives without file format overhead, registered via `IBuildingBlock`.
These are the 49 algorithms compared in the benchmark tool -- they operate on raw `byte[]` data with no container format.

| Id | Name | Family | Description |
| -- | ---- | ------ | ----------- |
| BB_Deflate | DEFLATE | Dictionary | LZ77 + Huffman, the algorithm inside gzip/zip/png |
| BB_Deflate64 | Deflate64 | Dictionary | Enhanced DEFLATE with 64KB window and extended codes |
| BB_Lz77 | LZ77 | Dictionary | Sliding-window dictionary compression with distance/length tokens |
| BB_Lz78 | LZ78 | Dictionary | Dictionary compression building phrases from input, predecessor to LZW |
| BB_Lzw | LZW | Dictionary | Lempel-Ziv-Welch dictionary coding, used in GIF and Unix compress |
| BB_Lzo | LZO1X | Dictionary | Extremely fast dictionary compression, optimized for decompression speed |
| BB_Lzss | LZSS | Dictionary | LZ77 variant with flag-bit encoding, omitting uncompressed references |
| BB_Lz4 | LZ4 | Dictionary | Extremely fast LZ77-family block compression optimized for speed |
| BB_Snappy | Snappy | Dictionary | Fast LZ77-family compression designed by Google for speed over ratio |
| BB_Brotli | Brotli | Dictionary | Modern LZ77+Huffman compression with static dictionary, designed by Google |
| BB_Lzma | LZMA | Dictionary | Lempel-Ziv-Markov chain Algorithm with range coding and sophisticated matching |
| BB_Lzx | LZX | Dictionary | LZ77+Huffman compression used in CAB and CHM archives (Microsoft) |
| BB_Xpress | XPRESS Huffman | Dictionary | LZ77+Huffman compression used in Windows (NTFS, WIM, Hyper-V) |
| BB_Lzh | LZH (LH5) | Dictionary | Lempel-Ziv with adaptive Huffman coding, used in LHA archives |
| BB_Arj | ARJ | Dictionary | Modified LZ77+Huffman used in ARJ archives |
| BB_Lzms | LZMS | Dictionary | LZ+Markov+Shannon compression with delta matching, used in Windows WIM |
| BB_Lzp | LZP | Dictionary | Lempel-Ziv Prediction using context-based match prediction |
| BB_Ace | ACE | Dictionary | LZ77+Huffman compression from the ACE archive format |
| BB_Rar | RAR5 | Dictionary | LZ+Huffman+PPM compression from the RAR5 archive format |
| BB_Sqx | SQX | Dictionary | LZ+Huffman compression from the SQX archive format |
| BB_ROLZ | ROLZ | Dictionary | Reduced-Offset LZ with context-based match tables |
| BB_PPM | PPM | Context Mixing | Prediction by Partial Matching, order-2 context modeling |
| BB_CTW | CTW | Context Mixing | Context Tree Weighting, optimal universal compression |
| BB_LZHAM | LZHAM | Dictionary | LZ77 + Huffman, inspired by Valve's LZHAM codec |
| BB_Lzs | LZS | Dictionary | Stac LZS (RFC 1967), 7/11-bit offset LZSS variant for networking |
| BB_Lzwl | LZWL | Dictionary | LZW with variable-length initial alphabet from digram analysis |
| BB_RePair | Re-Pair | Dictionary | Recursive Pairing, offline grammar-based compression |
| BB_842 | 842 | Dictionary | IBM 842 hardware compression with 2/4/8-byte template matching |
| BB_Huffman | Huffman | Entropy | Optimal prefix-free entropy coding using symbol frequencies |
| BB_Arithmetic | Arithmetic Coding | Entropy | Order-0 arithmetic coding with frequency table |
| BB_ShannonFano | Shannon-Fano | Entropy | Historical predecessor to Huffman, recursive frequency splitting |
| BB_Golomb | Golomb/Rice | Entropy | Optimal coding for geometric distributions, Rice when M is power-of-2 |
| BB_Fibonacci | Fibonacci Coding | Entropy | Universal code using Zeckendorf representation with '11' terminators |
| BB_FSE | FSE/tANS | Entropy | Table-based Asymmetric Numeral Systems, used in Zstd |
| BB_BPE | Byte Pair Encoding | Entropy | Iterative most-frequent pair replacement |
| BB_RangeCoding | Range Coding | Entropy | Byte-oriented arithmetic coding variant with carryless normalization |
| BB_rANS | rANS | Entropy | Range ANS entropy coder, used in AV1, LZFSE |
| BB_ExpGolomb | Exp-Golomb | Entropy | Exponential Golomb coding, used in H.264/H.265 |
| BB_Unary | Unary Coding | Entropy | Simplest universal code, encodes N as N ones followed by a zero |
| BB_EliasGamma | Elias Gamma | Entropy | Universal code for positive integers using unary length prefix |
| BB_EliasDelta | Elias Delta | Entropy | Universal code for positive integers, Gamma-codes the bit length |
| BB_Levenshtein | Levenshtein Coding | Entropy | Self-delimiting universal code with recursive length prefixing |
| BB_Tunstall | Tunstall Coding | Entropy | Variable-to-fixed code, dual of Huffman, dictionary of input phrases |
| BB_Dmc | DMC | Entropy | Dynamic Markov Compression, bit-level FSM with state cloning |
| BB_Bwt | BWT | Transform | Burrows-Wheeler Transform, reorders bytes for better compression |
| BB_Mtf | MTF | Transform | Move-to-Front Transform, converts repeated patterns to small values |
| BB_Delta | Delta | Transform | Delta filter, stores differences between consecutive bytes |
| BB_Rle | RLE | Transform | Run-Length Encoding, replaces repeated bytes with count+value pairs |
| BB_Dpcm | DPCM | Transform | Differential Pulse-Code Modulation, stores sample-to-sample differences |

---

## Supported Formats

### Capability Scale

The `Write` column in the tables below uses this four-level scale:

| Level         | Meaning                                                             | Example formats         |
| ------------- | ------------------------------------------------------------------- | ----------------------- |
| **Unsupported** | No descriptor exists.                                             | (anything not listed)   |
| **Read-Only** | Can list and extract; no creation.                                  | MPQ, NSIS, BIN/CUE, BTRFS |
| **WORM**      | Write-Once-Read-Many — can produce a fresh archive from inputs, but cannot modify an existing one. | ZIP, TAR, 7z (most formats) |
| **R/W**       | Can also add/replace/remove entries in an existing archive in place. | (none yet — future work) |

In tables, `Yes` = WORM (or better), `-` = Read-Only. When R/W support lands for any format, a third column will be introduced.

### Archive Formats

| Format      | Extensions     | Read | Write       | Methods                                                                          |
| ----------- | -------------- | ---- | ----------- | -------------------------------------------------------------------------------- |
| ZIP         | `.zip`         | Yes  | Yes         | Store, Deflate, Deflate64, Shrink, Reduce, Implode, BZip2, LZMA, PPMd, Zstd, AES |
| RAR         | `.rar`         | Yes  | Yes (v4/v5) | RAR v1-v5 decoders, solid, multi-volume, encryption, recovery                    |
| 7z          | `.7z`          | Yes  | Yes         | LZMA/LZMA2, Deflate, BZip2, PPMd, BCJ/BCJ2, AES-256, multi-volume                |
| TAR         | `.tar`         | Yes  | Yes         | POSIX/GNU/PAX, multi-volume                                                      |
| CAB         | `.cab`         | Yes  | Yes         | MSZIP, LZX, Quantum                                                              |
| LZH/LHA     | `.lzh`,`.lha`  | Yes  | Yes         | lh0-lh7, lzs, lh1-lh3 (adaptive Huffman), pm0-pm2                                |
| ARJ         | `.arj`         | Yes  | Yes         | Methods 0-4, garble encryption                                                   |
| ARC         | `.arc`         | Yes  | Yes         | Methods 0-9 (RLE, LZW, Squeeze, Huffman)                                         |
| ZOO         | `.zoo`         | Yes  | Yes         | LZW, LZH                                                                         |
| ACE         | `.ace`         | Yes  | Yes         | ACE 1.0/2.0, solid, sound/picture filters, Blowfish, recovery                    |
| SQX         | `.sqx`         | Yes  | Yes         | LZH, multimedia, audio, solid, AES-128, recovery                                 |
| CPIO        | `.cpio`        | Yes  | Yes         | Binary, odc, newc, CRC                                                           |
| AR          | `.ar`          | Yes  | Yes         | Unix archive                                                                     |
| WIM         | `.wim`         | Yes  | Yes         | LZX, XPRESS                                                                      |
| RPM         | `.rpm`         | Yes  | Yes         | CPIO payload                                                                     |
| DEB         | `.deb`         | Yes  | Yes         | AR+TAR with gz/xz/zst/bz2                                                        |
| Shar        | `.shar`        | Yes  | Yes         | Shell archive                                                                    |
| PAK         | `.pak`         | Yes  | Yes         | ARC-compatible                                                                   |
| HA          | `.ha`          | Yes  | Yes         | HSC/ASC arithmetic coding                                                        |
| ZPAQ        | `.zpaq`        | Yes  | Yes         | Context mixing, journaling                                                       |
| StuffIt     | `.sit`         | Yes  | Yes         | Multiple methods                                                                 |
| StuffIt X   | `.sitx`        | Yes  | Yes         | StuffIt X format — WORM emits valid `StuffIt!` envelope (detection-only; proprietary element-stream writer not implemented) |
| SquashFS    | `.sqfs`        | Yes  | Yes         | Filesystem image                                                                 |
| CramFS      | `.cramfs`      | Yes  | Yes         | Filesystem image                                                                 |
| NSIS        | `.exe`         | Yes  | Yes         | Installer extraction + WORM emits overlay-only data (no PE stub — not a functional installer) |
| Inno Setup  | `.exe`         | Yes  | Yes         | Installer extraction + WORM emits signature header (no PE stub — not a functional installer)  |
| DMS         | `.dms`         | Yes  | Yes         | Amiga disk archiver                                                              |
| LZX (Amiga) | `.lzx`         | Yes  | Yes         | Amiga LZX format                                                                 |
| Compact Pro | `.cpt`         | Yes  | Yes         | Classic Mac format                                                               |
| Spark       | `.spark`       | Yes  | Yes         | RISC OS format                                                                   |
| LBR         | `.lbr`         | Yes  | Yes         | CP/M format                                                                      |
| UHARC       | `.uha`         | Yes  | Yes         | LZP compression                                                                  |
| WAD         | `.wad`         | Yes  | Yes         | Doom WAD format                                                                  |
| WAD2/WAD3   | `.wad`         | Yes  | Yes         | Quake/Half-Life texture archive                                                  |
| XAR         | `.xar`         | Yes  | Yes         | Apple .pkg format (zlib TOC)                                                     |
| ALZip       | `.alz`         | Yes  | Yes         | Korean archive (Deflate)                                                         |
| VPK         | `.vpk`         | Yes  | Yes         | Valve game archive                                                               |
| BSA         | `.bsa`,`.ba2`  | Yes  | Yes         | Bethesda game archive                                                            |
| MPQ         | `.mpq`         | Yes  | Yes         | Blizzard game archive — WORM emits v1 with stored entries, encrypted hash + block tables, and a self-referential `(listfile)` |
| GRP         | `.grp`         | Yes  | Yes         | BUILD Engine (Duke Nukem 3D)                                                     |
| HOG         | `.hog`         | Yes  | Yes         | Descent game archive                                                             |
| BIG         | `.big`         | Yes  | Yes         | EA Games (Command & Conquer, FIFA)                                               |
| Godot PCK   | `.pck`         | Yes  | Yes         | Godot Engine resource pack                                                       |
| WARC        | `.warc`        | Yes  | Yes         | Web archive format — WORM emits one "resource" record per input file              |
| NDS         | `.nds`         | Yes  | Yes         | Nintendo DS ROM — WORM emits valid NitroFS (no ARM9/ARM7 boot code; not executable on hardware) |
| NSA         | `.nsa`         | Yes  | Yes         | NScripter archive — WORM writes stored entries (compression type 0)              |
| SAR         | `.sar`         | Yes  | Yes         | NScripter archive (uncompressed variant of NSA)                                  |
| PackIt      | `.pit`         | Yes  | -           | Classic Mac format                                                               |
| DiskDoubler | `.dd`          | Yes  | Yes         | Classic Mac compression — WORM stores data fork (method 0)                       |
| MSI         | `.msi`         | Yes  | Yes         | OLE Compound File — WORM produces a CFB envelope (not a functional Windows Installer DB) |
| PDF         | `.pdf`         | Yes  | Yes         | Image extraction + WORM via file attachments (EmbeddedFiles) — any file type roundtrips |
| TNEF        | `.tnef`,`.dat` | Yes  | Yes         | Outlook winmail.dat                                                              |
| Split File  | `.001`         | Yes  | Yes         | Multi-part file joining/splitting                                                |
| FreeArc     | `.arc`         | Yes  | Yes         | FreeArc archive                                                                  |
| CHM         | `.chm`         | Yes  | Yes         | Microsoft Compiled HTML Help — WORM stores files in section 0 (uncompressed); LZX compression available via options |

### ZIP-Derived Containers

These formats are ZIP archives with format-specific layout conventions. All delegate to the ZIP reader/writer; WORM (`Yes`) means a fresh container can be produced.

| Format | Extensions       | Read | Write | Notes                                |
| ------ | ---------------- | ---- | ----- | ------------------------------------ |
| JAR    | `.jar`           | Yes  | Yes   | Java archive                         |
| WAR    | `.war`           | Yes  | Yes   | Java web archive                     |
| EAR    | `.ear`           | Yes  | Yes   | Java enterprise archive              |
| APK    | `.apk`           | Yes  | Yes   | Android package                      |
| IPA    | `.ipa`           | Yes  | Yes   | iOS package                          |
| APPX   | `.appx`,`.msix`  | Yes  | Yes   | Windows package                      |
| XPI    | `.xpi`           | Yes  | Yes   | Firefox extension                    |
| CRX    | `.crx`           | Yes  | Yes   | Chrome extension — write produces an unsigned CRX3 envelope (browser will reject signature; toolkit roundtrip works) |
| EPUB   | `.epub`          | Yes  | Yes   | eBook                                |
| MAFF   | `.maff`          | Yes  | Yes   | Mozilla Archive Format               |
| KMZ    | `.kmz`           | Yes  | Yes   | Google Earth                         |
| NuPkg  | `.nupkg`         | Yes  | Yes   | NuGet package                        |
| DOCX   | `.docx`          | Yes  | Yes   | OOXML Word                           |
| XLSX   | `.xlsx`          | Yes  | Yes   | OOXML Excel                          |
| PPTX   | `.pptx`          | Yes  | Yes   | OOXML PowerPoint                     |
| ODT    | `.odt`           | Yes  | Yes   | OpenDocument Text                    |
| ODS    | `.ods`           | Yes  | Yes   | OpenDocument Spreadsheet             |
| ODP    | `.odp`           | Yes  | Yes   | OpenDocument Presentation            |
| CBZ    | `.cbz`           | Yes  | Yes   | Comic book ZIP                       |
| CBR    | `.cbr`           | Yes  | Yes   | Comic book RAR — delegates to RarWriter                                      |

### OLE2 Compound File Variants

Microsoft binary office formats built on the OLE2 / Compound File Binary (CFB) container.

| Format    | Extensions  | Read | Write | Notes                                            |
| --------- | ----------- | ---- | ----- | ------------------------------------------------ |
| DOC       | `.doc`      | Yes  | Yes   | Word 97-2003 — WORM produces a structurally-valid CFB envelope (not a real Word document; see note below) |
| XLS       | `.xls`      | Yes  | Yes   | Excel 97-2003 — WORM (CFB envelope, not a real workbook)                            |
| PPT       | `.ppt`      | Yes  | Yes   | PowerPoint 97-2003 — WORM (CFB envelope, not a real presentation)                   |
| MSG       | `.msg`      | Yes  | Yes   | Outlook message — WORM (CFB envelope, not real MAPI properties)                     |
| Thumbs.db | `Thumbs.db` | Yes  | Yes   | Windows thumbnail cache — WORM (CFB envelope, not a real Catalog/thumbnail layout)  |
| MSI       | `.msi`      | Yes  | Yes   | Windows Installer — WORM produces a CFB envelope (not a functional Installer DB)    |

**Note on DOC/XLS/PPT/MSG/Thumbs.db WORM creation:** The shared `CfbWriter` produces a structurally-valid OLE Compound File container holding the input files as named streams. The result roundtrips through our reader (and other permissive CFB tools like libgsf, Apache POI). It is **not** a Word/Excel/PowerPoint/Outlook document — those require generating each application's internal binary stream layout (e.g. Word's `WordDocument` + `1Table`/`0Table` records), which is well out of scope for a compression toolkit. Limitations: ~6.8 MB total file size cap (109 FAT sectors, no DIFAT chain), single root storage (no nested storages), stream names limited to 31 UTF-16 characters.

### Disc Image Formats

| Format  | Extensions    | Read | Write | Notes                          |
| ------- | ------------- | ---- | ----- | ------------------------------ |
| BIN/CUE | `.bin`,`.cue` | Yes  | Yes   | Raw disc image — WORM emits ISO 9660 cooked sectors (auto-detected by reader; CUE sheet is the user's responsibility) |
| MDF     | `.mdf`        | Yes  | Yes   | Alcohol 120% — WORM emits ISO 9660 (MDS sidecar not produced)              |
| NRG     | `.nrg`        | Yes  | Yes   | Nero — WORM emits ISO 9660 with NER5 footer                                |
| CDI     | `.cdi`        | Yes  | Yes   | DiscJuggler — WORM emits ISO 9660 with CDI v2 footer                       |
| DMG     | `.dmg`        | Yes  | Yes   | Apple disk image — WORM emits raw mish blocks per partition (no zlib/bz2/lzfse encoding); read-side handles all four compressions |

### Filesystem Image Formats

| Format      | Extensions | Read | Write | Notes                             |
| ----------- | ---------- | ---- | ----- | --------------------------------- |
| ISO 9660    | `.iso`     | Yes  | Yes   | CD/DVD filesystem                 |
| UDF         | `.udf`     | Yes  | Yes   | Universal Disk Format — WORM emits minimal UDF 1.02 with flat file layout |
| FAT12/16/32 | `.img`     | Yes  | Yes   | DOS/Windows filesystem            |
| exFAT       | `.img`     | Yes  | Yes   | Extended FAT                      |
| NTFS        | `.img`     | Yes  | Yes   | Windows NT filesystem (LZNT1)     |
| HFS         | `.img`     | Yes  | Yes   | Classic Mac filesystem            |
| HFS+        | `.img`     | Yes  | Yes   | Modern Mac filesystem (B-tree)    |
| MFS         | `.img`     | Yes  | Yes   | Original Macintosh File System    |
| ext2/3/4    | `.img`     | Yes  | Yes   | Linux filesystem                  |
| Btrfs       | `.img`     | Yes  | Yes   | Linux CoW filesystem — WORM with root tree + FS tree + inline EXTENT_DATA items (single-subvolume) |
| XFS         | `.img`     | Yes  | Yes   | Linux high-performance filesystem — WORM with short-form dirs + extent data (big-endian)       |
| JFS         | `.img`     | Yes  | Yes   | IBM Journaled File System — WORM with inline dtree + xtree extents                            |
| ReiserFS    | `.img`     | Yes  | Yes   | ReiserFS v3 — WORM with single leaf S+ tree (directory entries + direct data items)             |
| F2FS        | `.img`     | Yes  | Yes   | Flash-Friendly File System — WORM with NAT + dentry block + direct data blocks                  |
| UFS         | `.img`     | Yes  | Yes   | Unix File System                                                                               |
| ZFS         | `.img`     | Yes  | Yes   | OpenZFS — WORM produces valid uberblock label (detection-only; file extraction requires future MOS/ZAP support) |
| APFS        | `.img`     | Yes  | Yes   | Apple File System — WORM produces valid NX container superblock (listing-only; file extraction requires future extent support) |
| ROMFS       | `.romfs`   | Yes  | Yes   | Linux ROM filesystem              |
| MinixFS     | `.minix`   | Yes  | Yes   | Minix v1/v2/v3 filesystem         |
| VDFS        | `.vdfs`    | Yes  | Yes   | PlayStation Vita filesystem       |
| DoubleSpace | `.cvf`     | Yes  | Yes   | MS-DOS DoubleSpace/DriveSpace CVF |

### Virtual Disk Image Formats

| Format | Extensions | Read | Write | Notes                              |
| ------ | ---------- | ---- | ----- | ---------------------------------- |
| VHD    | `.vhd`     | Yes  | Yes   | VirtualPC/Hyper-V                  |
| VMDK   | `.vmdk`    | Yes  | Yes   | VMware                             |
| VDI    | `.vdi`     | Yes  | Yes   | VirtualBox (dynamic sparse)        |
| QCOW2  | `.qcow2`   | Yes  | Yes   | QEMU — WORM wraps raw disk with L1/L2 table mapping (uncompressed clusters, v2 format) |

### Retro Computing Formats

| Format   | Extensions | Read | Write | Notes                      |
| -------- | ---------- | ---- | ----- | -------------------------- |
| ADF      | `.adf`     | Yes  | Yes   | Amiga disk image (OFS/FFS) |
| D64      | `.d64`     | Yes  | Yes   | C64 1541 disk image        |
| D71      | `.d71`     | Yes  | Yes   | C128 1571 disk image       |
| D81      | `.d81`     | Yes  | Yes   | C128 1581 disk image       |
| T64      | `.t64`     | Yes  | Yes   | C64 tape image             |
| TAP      | `.tap`     | Yes  | Yes   | ZX Spectrum tape image     |
| CPC DSK  | `.dsk`     | Yes  | Yes   | Amstrad CPC disk image     |
| MSA      | `.msa`     | Yes  | -     | Atari ST disk image        |
| TR-DOS   | `.trd`     | Yes  | -     | ZX Spectrum TR-DOS disk    |
| PackDisk | `.pdsk`    | Yes  | Yes   | Amiga PackDisk — WORM writes stored tracks (no XPK encoder needed). Same writer covers DCS / xDisk / xMash via different magics. |
| Wrapster | -          | Yes  | -     | MP3 wrapper                |
| LhF      | `.lhf`     | Yes  | Yes   | Amiga LhFloppy disk (LZH-compressed tracks) |
| ZAP      | `.zap`     | Yes  | Yes   | Amiga disk archiver — WORM writes stored tracks |
| AMPK     | -          | Yes  | -     | Amiga AMPK                 |
| IFF-CDAF | -          | Yes  | -     | IFF-CDAF archive           |
| UMX      | `.umx`     | Yes  | Yes   | Unreal package — WORM emits valid header (detection-only; full compact-index encoding not implemented) |

### Compression Stream Formats

| Format      | Extensions        | Compress | Decompress |
| ----------- | ----------------- | -------- | ---------- |
| Gzip        | `.gz`             | Yes      | Yes        |
| BZip2       | `.bz2`            | Yes      | Yes        |
| XZ          | `.xz`             | Yes      | Yes        |
| Zstandard   | `.zst`            | Yes      | Yes        |
| LZ4         | `.lz4`            | Yes      | Yes        |
| Brotli      | `.br`             | Yes      | Yes        |
| Snappy      | `.sz`,`.snappy`   | Yes      | Yes        |
| LZOP        | `.lzo`            | Yes      | Yes        |
| Compress    | `.Z`              | Yes      | Yes        |
| LZMA        | `.lzma`           | Yes      | Yes        |
| Lzip        | `.lz`             | Yes      | Yes        |
| Zlib        | `.zlib`           | Yes      | Yes        |
| SZDD        | `.sz_`            | Yes      | Yes        |
| KWAJ        | -                 | Yes      | Yes        |
| RZIP        | `.rz`             | Yes      | Yes        |
| MacBinary   | `.bin`            | Yes      | Yes        |
| BinHex      | `.hqx`            | Yes      | Yes        |
| Squeeze     | `.sqz`            | Yes      | Yes        |
| PowerPacker | `.pp`             | Yes      | Yes        |
| ICE Packer  | `.ice`            | Yes      | Yes        |
| PackBits    | `.packbits`       | Yes      | Yes        |
| Yaz0        | `.yaz0`,`.szs`    | Yes      | Yes        |
| BriefLZ     | `.blz`            | Yes      | Yes        |
| RNC         | `.rnc`            | Yes      | Yes        |
| RefPack     | `.qfs`,`.refpack` | Yes      | Yes        |
| aPLib       | `.aplib`          | Yes      | Yes        |
| LZFSE       | `.lzfse`          | Yes      | Yes        |
| Freeze      | `.f`,`.freeze`    | Yes      | Yes        |
| UuEncoding  | `.uu`,`.uue`      | Yes      | Yes        |
| yEnc        | `.yenc`           | Yes      | Yes        |
| Density     | `.density`        | Yes      | Yes        |
| LZG         | `.lzg`            | Yes      | Yes        |
| BCM         | `.bcm`            | Yes      | Yes        |
| BSC         | `.bsc`            | Yes      | Yes        |
| BALZ        | `.balz`           | Yes      | Yes        |
| CSC         | `.csc`            | Yes      | Yes        |
| Zling       | `.zling`          | Yes      | Yes        |
| Lizard      | `.lizard`         | Yes      | Yes        |
| QuickLZ     | `.quicklz`        | Yes      | Yes        |
| cmix        | `.cmix`           | Yes      | Yes        |
| MCM         | `.mcm`            | Yes      | Yes        |
| PAQ8        | `.paq8`           | Yes      | Yes        |
| SWF         | `.swf`            | Yes      | Yes        |
| Crunch      | `.cru`            | Yes      | Yes        |
| PPMd        | `.pmd`            | Yes      | Yes        |
| LZHAM       | `.lzham`          | Yes      | Yes        |
| LZS         | `.lzs`            | Yes      | Yes        |
| FLAC        | `.flac`           | Yes      | Yes        |

### Compound Formats

`tar.gz`, `tar.bz2`, `tar.xz`, `tar.zst`, `tar.lz4`, `tar.lz`, `tar.br`

### Modern Packaging Formats

| Format    | Extensions     | Read | Write | Notes                                                                          |
| --------- | -------------- | ---- | ----- | ------------------------------------------------------------------------------ |
| AppImage  | `.AppImage`    | Yes  | -     | ELF stub + appended SquashFS; offset located by ELF section-end + magic scan   |
| Snap      | `.snap`        | Yes  | -     | SquashFS image with `meta/snap.yaml`; reuses `FileSystem.SquashFs`             |
| MSIX      | `.msix`,`.msixbundle` | Yes | - | Modern Windows app package; mirrors APPX (ZIP + AppxManifest.xml)              |
| ESD       | `.esd`         | Yes  | -     | Windows Update encrypted-LZMS WIM variant; shares `MSWIM\0\0\0` magic, detected by extension |
| Split WIM | `.swm`,`.swmN` | Yes  | -     | Multi-part WIM volume (one part of an N-way split set)                         |
| WACZ      | `.wacz`        | Yes  | -     | Web Archive Collection Zipped — ZIP around WARC files with `datapackage.json` + page index |
| Wheel     | `.whl`         | Yes  | -     | Python wheel (PEP 427) — ZIP with `dist-info/METADATA`, `WHEEL`, `RECORD`      |
| Gem       | `.gem`         | Yes  | -     | Ruby gem package — TAR with `metadata.gz`, `data.tar.gz`, `checksums.yaml.gz`  |
| Crate     | `.crate`       | Yes  | -     | Rust crate package — TAR.GZ with single `name-version/` directory containing `Cargo.toml` |

### Firmware + Embedded Formats

| Format             | Extensions                       | Read | Write | Notes                                                                |
| ------------------ | -------------------------------- | ---- | ----- | -------------------------------------------------------------------- |
| U-Boot uImage      | `.uimg`,`.img`,`.bin`            | Yes  | -     | 64-byte legacy header + body; reports OS/arch/comp; auto-decompresses payload when possible |
| Device Tree Blob   | `.dtb`,`.dtbo`                   | Yes  | -     | FDT v17, walks property tree as pseudo-archive (one entry per leaf property) |
| Intel HEX          | `.hex`,`.ihex`                   | Yes  | -     | ASCII firmware records, decoded to flat `firmware.bin` + metadata    |
| Motorola S-Record  | `.s19`,`.s28`,`.s37`,`.srec`,`.mot` | Yes | -    | 16/24/32-bit address records, same decoded shape as Intel HEX        |
| TI-TXT             | -                                | Yes  | -     | MSP430 firmware text, address blocks                                 |
| UEFI Firmware Vol  | `.fv`,`.fd`,`.rom`,`.bin`        | Yes  | -     | `_FVH` at offset 40, walks FFS files (RAW/PEIM/DRIVER/PEI_CORE/etc.) |

### Disk Image + Forensics Formats

| Format | Extensions               | Read | Write | Notes                                                                       |
| ------ | ------------------------ | ---- | ----- | --------------------------------------------------------------------------- |
| VHDX   | `.vhdx`                  | Yes  | -     | Hyper-V modern; surfaces File Type ID + 2 headers + 2 region tables (BAT walk deferred) |
| EWF/E01 | `.e01`,`.ewf`,`.l01`    | Yes  | -     | EnCase forensic image; section-chain walker, header2 acquisition text + MD5/SHA1 |
| G64    | `.g64`                   | Yes  | -     | Commodore GCR-encoded track dump (1541)                                     |
| NIB    | `.nib`                   | Yes  | -     | Commodore raw nibble track dump                                             |

### Scientific + ML Formats

| Format | Extensions       | Read | Write | Notes                                                                      |
| ------ | ---------------- | ---- | ----- | -------------------------------------------------------------------------- |
| NumPy NPY | `.npy`        | Yes  | -     | Single ndarray header + raw bytes                                          |
| NumPy NPZ | `.npz`        | Yes  | -     | ZIP of NPYs                                                                |
| NIfTI-1/2 | `.nii`,`.nii.gz` | Yes | -    | Medical imaging (MRI); 352-byte v1 / 540-byte v2 header + voxel data; transparent gzip |
| HDF4   | `.hdf`,`.hdf4`,`.h4` | Yes | -   | DD linked-list walker, tag histogram + per-DD entry                         |
| ONNX   | `.onnx`          | Yes  | -     | Pure-C# protobuf reader (~400 LOC), surfaces graph initializers as entries |

### CAD / 3D Formats

| Format  | Extensions | Read | Write | Notes                                                                |
| ------- | ---------- | ---- | ----- | -------------------------------------------------------------------- |
| STL     | `.stl`     | Yes  | -     | ASCII + binary; triangle count, bounding box, name                   |
| PLY     | `.ply`     | Yes  | -     | Stanford triangle format (ASCII, binary LE/BE), element schema       |
| DXF     | `.dxf`     | Yes  | -     | AutoCAD ASCII; section list + entity histogram (LINE/CIRCLE/POLYLINE/TEXT) |
| Collada | `.dae`     | Yes  | -     | XML 3D interchange; surfaces top-level libraries as XML fragments    |
| 3DS     | `.3ds`     | Yes  | -     | Autodesk binary chunks; mesh count, vertex/face count, chunk-ID histogram |

### Medical Imaging

| Format    | Extensions | Read | Write | Notes                                                                 |
| --------- | ---------- | ---- | ----- | --------------------------------------------------------------------- |
| DICOM     | `.dcm`     | Yes  | -     | Single DICOM image                                                    |
| DICOMDIR  | `.dcmdir`,`DICOMDIR` | Yes | -    | Multi-study patient/series index referencing sibling DICOM files     |

### Streaming & Subtitle Formats

| Format   | Extensions            | Read | Write | Notes                                                                            |
| -------- | --------------------- | ---- | ----- | -------------------------------------------------------------------------------- |
| SUP      | `.sup`                | Yes  | -     | Blu-ray Presentation Graphic Stream subtitle segments, grouped by epoch          |
| VobSub   | `.idx` + `.sub`       | Yes  | -     | DVD subtitle pair; parses `.idx` palette/timestamps + slices sibling `.sub` PES  |
| M3U8/HLS | `.m3u8`,`.m3u`        | Yes  | -     | HTTP Live Streaming manifest — master variant list or media segment list         |
| MPEG-TS  | `.ts`,`.m2ts`,`.mts`  | Yes  | -     | MPEG-2 Transport Stream demuxed into per-PID elementary streams (video/audio/data) |

### Audio Codecs

Standalone audio codecs live under `Codec.*` projects (separate from container-format descriptors). Each exposes a static `Decompress(Stream input, Stream output)` producing interleaved little-endian PCM and a `ReadStreamInfo` for metadata-only access. Encoders are explicitly out-of-scope for the new codecs — only the legacy ones (PCM/FLAC/A-law/μ-law/GSM/ADPCM/MIDI) ship encoders.

| Codec     | Project          | Encoder | Decoder state                                                                  |
| --------- | ---------------- | ------- | ------------------------------------------------------------------------------ |
| PCM       | `Codec.Pcm`      | Yes     | Production — raw integer PCM up to 32-bit                                      |
| FLAC      | `Codec.Flac`     | Yes     | Production — FIXED + LPC subframes, all sample rates / bit depths              |
| A-law     | `Codec.ALaw`     | Yes     | Production — G.711                                                             |
| μ-law     | `Codec.MuLaw`    | Yes     | Production — G.711                                                             |
| GSM 06.10 | `Codec.Gsm610`   | Yes     | Production — full RPE-LTP                                                      |
| IMA ADPCM | `Codec.ImaAdpcm` | Yes     | Production — Microsoft + Apple variants                                        |
| MS ADPCM  | `Codec.MsAdpcm`  | Yes     | Production — WAV format 0x0002                                                 |
| MIDI      | `Codec.Midi`     | Yes     | Production — SMF 0/1/2 with all standard meta + channel events                 |
| **MP3**   | `Codec.Mp3`      | -       | **Header + framing complete; bit-exact decode unverified** — full minimp3 port (1469 LOC, scalar) covering MPEG-1/2/2.5 Layer III, MS+intensity stereo, ID3v2 skip, Xing VBR. Frame header parser + layer/bitrate-table lookup + Layer I/II rejection all pass tests. End-to-end PCM decode against a reference clip is deferred until an MP3 test vector lands in `test-corpus/` (no `lame`/`ffmpeg` was available on the build host). |
| **Vorbis**| `Codec.Vorbis`   | -       | **Partial** — full stb_vorbis structural port (1295 LOC) covering Ogg page reassembly, codebooks (lookup 0/1/2), floor 1, residue 0/1/2, channel coupling, IMDCT (direct O(N²) — slow but correct on 2048-sample blocks). Floor 0 explicitly throws `NotSupportedException`. End-to-end test marked `Inconclusive` until a `.ogg` test vector lands in `test-corpus/`. |
| **Opus**  | `Codec.Opus`     | -       | **Framing only** — Ogg page walker + OpusHead/OpusTags + TOC byte + frame packing modes 0/1/2/3 + range decoder (`ec_dec`) all real (12 tests pass). CELT and SILK pipelines are stubs that emit silence at the correct sample count. Hybrid mode throws `NotSupportedException`. |
| **AAC-LC**| `Codec.Aac`      | -       | **Framing only** — ADTS frame parser + AudioSpecificConfig + element dispatcher + profile gating real (11 tests pass). Spectral pipeline + Huffman tables + IMDCT + filterbank are scaffolded but the spectral data tables are TODO; end-to-end test `[Ignore]`-d to avoid silently emitting garbage PCM. HE-AAC v1/v2 + Main/SSR/LTP/ER all throw `NotSupportedException`. |

**Implementation philosophy:** the four new audio codecs (Mp3 / Vorbis / Opus / AAC-LC) ship under the project's "no toy implementations" rule — partial state is documented openly (in class summaries, in `Assert.Ignore` messages, and in this table) rather than silently producing wrong PCM. Future work: bit-pack debugging for Mp3, real CELT/SILK for Opus, spectral table population for AAC, and reference test-vector validation across all four when audio test corpora are added.

### Executable Packer Detection & Unpacking

CompressionWorkbench treats executable packers (UPX, the demoscene compressors, classic DOS packers, modern PE protectors) as pseudo-archives — they get the same `List` / `Extract` interface as ZIP or TAR.

| Packer | Container | Detection | In-process unpack |
|--------|-----------|-----------|-------------------|
| **UPX** | PE / ELF / Mach-O | Section names + `UPX!` header + tampering-resistant structural fingerprint | NRV2B/D/E LE32 + LZMA |
| **PKLITE** | DOS .exe | `PKLITE Copr.` copyright string | No (use `pklite -x`) |
| **LZEXE** | DOS .exe | `LZ91` / `LZ09` signature | No |
| **Petite** | Win32 PE | `.petite*` section + `Petite` literal | No |
| **Shrinkler** | Amiga HUNK | HUNK magic + `Shrinkler` literal | No |
| **FSG** | Win32 PE | `FSG!` magic | No |
| **MEW** | Win32 PE | `.MEW*` section name | No |
| **MPRESS** | Win32 PE / Linux ELF | `.MPRESS1/2` section + `MATCODE` literal | No |
| **Crinkler** | Win32 PE | `Crinkler` literal | No |
| **kkrunchy** | Win32 PE | `kkrunchy` literal | No |
| **ASPack** | Win32 PE | `.aspack` / `.adata` section + `ASPack` literal | No |
| **NsPack** | Win32 PE | `.nsp*` section + `NsPack` literal | No |
| **Yoda's Crypter** | Win32 PE | `.yC` / `yC` section + `Yoda's` literal | No |
| **ASProtect** | Win32 PE | `ASProtect` literal | No |
| **Themida** | Win32 PE | `Themida` / `WinLicense` literal | No (best-effort) |
| **VMProtect** | Win32 PE | `.vmp*` section + `VMProtect` literal | No (best-effort) |

UPX detection is hardened against tampered binaries: the structural fingerprint (BSS-style first section + RWX flags + entry point in last section + payload entropy ≥ 7.5) catches binaries where the `UPX!` magic and section names have been wiped. A brute-force PackHeader scan validates `format`/`method`/`uLen`/`cLen`/`level`/`version` even when the magic bytes are zeroed.

See [docs/PACKERS.md](docs/PACKERS.md) for the full coverage matrix and the detection-confidence model.

---

## Compression.Analysis - Binary Analysis Engine

The analysis engine provides tools for identifying and characterizing unknown binary data:

- **Signature Scanner**: Magic bytes detection for all registered format signatures (auto-discovered from FormatRegistry)
- **Algorithm Fingerprinting**: Statistical fingerprinting to identify compression algorithms
- **Trial Decompression**: Attempts all registered stream decompressors to find valid streams
  - Parallel trial decompression with per-trial timeout and early termination on high-confidence match
- **Chain Reconstruction**: Discovers layered compression (e.g., gzip(bzip2(data)))
- **Entropy Mapping**: Per-region entropy profiling with boundary detection
  - Multi-resolution entropy pyramid (64KB/8KB/1KB/256B scales)
  - CUSUM binary segmentation for change-point detection
  - KL-divergence + chi-square boundary validation
  - Edge sharpening (1D Canny-style gradient analysis)
- **String Extraction**: ASCII/UTF-8/UTF-16 string search with regex support
- **Structure Templates**: ImHex/010-style `.cwbt` template language for parsing binary structures
  - Built-in templates for ZIP, PNG, BMP, ELF, Gzip headers
  - Field types: u8-u64, i8-i64, f16/f32/f64 (LE/BE), char/u8 arrays, BCD, fixed-point, color, date/time, network types
  - Dynamic length via field references or repeat-to-EOF
- **Streaming Analysis**: Analyze arbitrarily large files without full materialization
  - Reads the first 64KB for magic/header detection
  - Computes entropy and byte statistics by processing the stream in 64KB chunks
  - Returns per-chunk entropy profiles for the entire file
- **Black-Box Tool Integration**: External tool discovery and cross-validation
  - `ExternalToolRunner`: Invoke external tools (7z, binwalk, file, trid) and capture output
  - `ToolOutputParser`: Parse structured output from known tools (7z `l -slt`, `file --mime`)
  - `CrossValidator`: Compare our detection results with external tool results, flag disagreements
  - `FallbackDecompressor`: If our decompressor fails, try external tool as fallback
  - Auto-detection of available tools on PATH

### Format Detection Pipeline

The format detection system uses a multi-stage pipeline:

1. **Magic bytes**: Scan the first bytes for known format signatures (hash-indexed for O(n) performance)
2. **Trial decompression**: Attempt all registered stream decompressors in parallel; early-terminate on low-entropy output
3. **Extension fallback**: Match file extension against known format associations
4. **Deep probing**: Validate candidate formats by parsing headers, checking internal structure, and testing integrity

### Auto-Extraction

The `AutoExtractor` recursively extracts nested containers:

- Archives inside archives (e.g., a ZIP inside a TAR inside a GZIP)
- Disk images with partition tables: VHD/VMDK/QCOW2/VDI -> MBR/GPT partitions -> NTFS/FAT/ext4 filesystems -> files
- Configurable maximum depth (default 5) and file size limits

### Batch Analysis

The `BatchAnalyzer` scans entire directories in parallel using `Parallel.ForEach`, detecting the format of each file and producing aggregate statistics (format distribution, total compressed/uncompressed sizes).

### Partition Table Support

- **MBR parser**: Read 4 primary partition entries at offset 0x1BE, walk extended/logical partition chains
- **GPT parser**: Parse EFI PART header at LBA 1, enumerate partition entry array
- **Partition type database**: Map type bytes (MBR) and GUIDs (GPT) to filesystem names
- Recursive descent: disk image -> partition table -> filesystem -> archive chains via `--recursive` CLI flag

---

## Compression.CLI - `cwb` Command-Line Tool

A universal archive tool with smart conversion and optimal re-encoding.

### Commands

| Command                        | Alias   | Description                             |
| ------------------------------ | ------- | --------------------------------------- |
| `list <archive>`               | `l`     | List contents of an archive             |
| `extract <archive> [files...]` | `x`     | Extract files from an archive           |
| `create <archive> <files...>`  | `c`     | Create a new archive                    |
| `test <archive>`               | `t`     | Test archive integrity                  |
| `info <archive>`               | -       | Show detailed archive information       |
| `convert <input> <output>`     | -       | Convert between archive formats         |
| `optimize <input> <output>`    | `opt`   | Re-encode with optimal compression      |
| `benchmark <file>`             | `bench` | Benchmark all building blocks on a file |
| `analyze <file>`               | -       | Run binary analysis                     |
| `formats`                      | -       | List all supported formats              |

### Examples

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
```

### 3-Tier Conversion Model

| Tier | Strategy                                     | Example                         |
| ---- | -------------------------------------------- | ------------------------------- |
| 1    | Bitstream transfer (zero decompression)      | `gz` <-> `zlib`, `zip` <-> `gz` |
| 2    | Container restream (decompress wrapper only) | `tar.gz` -> `tar.xz`            |
| 3    | Full recompress (extract + re-encode)        | `zip` -> `7z`                   |

### Method+ System

Append `+` to any method for optimal encoding: `deflate+` uses Zopfli, `lzma+` uses Best, `lz4+` uses HC.

---

## Compression.UI - WPF Archive Browser

- File list with icons, columns (name, size, compressed, ratio, method, modified)
- Open/extract/create/test archives
- Preview window with text + hex views
- Properties dialog with compression ratio visualization
- Binary analysis wizard with toolbar-driven tools:
  - **Scan Results**: Signature detection
  - **Fingerprints**: Algorithm identification
  - **Entropy Map**: Per-region entropy with boundary detection
  - **Trial Decompress**: Automatic decompressor probing
  - **Chain**: Multi-layer compression reconstruction
  - **Statistics**: Full randomness/distribution analysis (shared control)
  - **Strings**: ASCII/regex string extraction
  - **Structure**: Binary template parsing
- Hex viewer with byte-wise auto-width and frequency-based coloring
- Benchmark tool: compare building block algorithms across data patterns
- Explorer context menu integration (`Compression.Shell`)

---

## Reverse Engineering

CompressionWorkbench can reverse-engineer unknown compression tools and file formats through two complementary approaches.

### Black-Box Tool Probing (CLI)

```bash
cwb reverse-engineer MyTool.exe "{input} {output}"
cwb reverse-engineer packer.exe "--pack {input} --out {output}" --timeout 10000
```

The `reverse-engineer` command (alias `reveng`) runs the target tool with approximately 40 controlled probe inputs (empty, single byte, incrementing patterns, text, random data, various sizes from 0 to 64KB) and collects the outputs. It then cross-correlates all outputs to discover:

- **Magic bytes** -- common header/footer bytes across all outputs
- **Size fields** -- offsets where input or output size appears (LE/BE, 2/4/8 byte)
- **Compression algorithm** -- trial decompression of the payload region against all 49 building blocks
- **Filename storage** -- whether the original filename is embedded (UTF-8, UTF-16)
- **Determinism** -- whether identical inputs always produce identical outputs
- **Payload entropy** -- whether the payload is compressed, encoded, or stored raw

The argument template must include `{input}` and `{output}` placeholders for the input and output file paths.

### WPF Wizard

The GUI provides the same capability via **Tools > Reverse Engineer Format** with a step-by-step wizard, progress reporting, and a structured results view.

### Static Analysis Mode

When you have archive files with known original content but no tool to run, use the `StaticFormatAnalyzer`. It accepts pairs of (original content, archive file) and locates where the content appears inside the archive -- verbatim or compressed with any known building block. It infers header/footer structure, size fields, and compression algorithm without ever executing an external tool.

---

## Tool Templates

Tool templates let you register external CLI tools for use from `cwb` and the WPF UI. Templates are stored in a JSON config file (default: `~/.cwb-tools.json`).

### Commands

```bash
cwb tool init                              # Create default config with common tools
cwb tool list                              # Show all registered templates
cwb tool add my-zstd zstd "-d -c {input}" --action decompress --ext .zst --stdout
cwb tool add my-7z 7z "x {input} -o{outputDir} -y" --ext .rar .zip .7z
cwb tool run archive.rar                   # Run best matching template
cwb tool run file.bin -t binwalk-scan      # Run a specific template by name
cwb tool remove my-zstd                    # Remove a template
```

### Placeholders

| Placeholder   | Meaning                                |
| ------------- | -------------------------------------- |
| `{input}`     | Path to the input file                 |
| `{output}`    | Path to the output file                |
| `{outputDir}` | Directory for extracted files          |

### JSON Config Format

The config file is a JSON array of template objects:

```json
[
  {
    "name": "my-7z-extract",
    "executable": "7z",
    "arguments": "x \"{input}\" -o\"{outputDir}\" -y",
    "action": "extract",
    "extensions": [],
    "description": "Extract any archive with 7-Zip"
  },
  {
    "name": "gzip-decompress",
    "executable": "gzip",
    "arguments": "-d -c \"{input}\"",
    "action": "decompress",
    "extensions": [".gz"],
    "captureStdout": true,
    "description": "Decompress gzip via stdout"
  }
]
```

Each template specifies: `name` (unique identifier), `executable` (tool name or path), `arguments` (with placeholders), `action` (extract/decompress/identify/list/compress), `extensions` (file types it handles, empty for any), and optional flags like `captureStdout`, `pipeStdin`, and `timeoutMs`.

The `cwb tool init` command pre-populates the config with templates for 7z, gzip, bzip2, xz, zstd, tar, file, and binwalk.

---

## Heatmap Explorer

The Heatmap Explorer provides a 16x16 color-coded grid for visual binary file exploration. Each of the 256 cells represents a proportional region of the file.

### Color Scheme

| Color  | Meaning                                          | Entropy Range |
| ------ | ------------------------------------------------ | ------------- |
| Blue   | Low entropy (zeros, padding, simple headers)     | 0.0 -- 3.0   |
| Green  | Structured data (tables, records, text)          | 3.0 -- 5.5   |
| Orange | Compressed data                                  | 5.5 -- 7.5   |
| Red    | Random/encrypted data (incompressible)           | 7.5 -- 8.0   |
| Purple | Known format signature detected at block start   | Any           |

### Navigation

- **Click** any cell to drill into that region (subdivides into another 16x16 grid)
- **Back** button returns to the previous zoom level
- **Root** button returns to the full-file view
- **Hover** shows offset, size, entropy, unique byte count, and detected signature
- **Extract** button saves the selected region to a file (available when a signature is detected)

### Access

- **Analyzer tab**: Heatmap tab in the binary analysis wizard
- **Standalone**: Tools > Heatmap Explorer in the WPF application

The explorer only reads sample bytes from each block, so it handles arbitrarily large files without loading them into memory.

---

## End-to-End Testing

The test suite includes three tiers of external validation beyond the standard self-round-trip tests.

### Self Round-Trip

All formats that support both create and extract are tested by creating an archive, extracting it, and verifying the output matches the original data. These run as part of the normal `dotnet test` suite.

### External Tool Interop (`Category=EndToEnd`)

Tests that verify our output is readable by external tools and vice versa. Uses dynamic tool discovery via PATH and common install locations. Gracefully skips tests when tools are unavailable.

Covered tools: **7z**, **gzip**, **bzip2**, **xz**, **zstd**, **lz4**, **tar**.

Both directions are tested for each format:
1. Create with our library, read with the external tool
2. Create with the external tool, read with our library

```bash
dotnet test --filter "Category=EndToEnd"
```

### .NET BCL Interop

Tests that verify interoperability with `System.IO.Compression` classes (`GZipStream`, `DeflateStream`, `BrotliStream`, `ZipArchive`) to ensure our output is compatible with the .NET runtime.

### OS Integration (`Category=OsIntegration`)

Tests against native OS tools that are only available on specific platforms:

- **Windows**: PowerShell `Compress-Archive`/`Expand-Archive`, Windows `tar`, `certutil`, `Mount-DiskImage`, DISM
- **Linux**: `mtools` (mdir/mcopy for FAT images), `genisoimage` (ISO creation), `qemu-img` (disk image validation), `debugfs` (ext4), `cpio`

```bash
dotnet test --filter "Category=OsIntegration"
```

Tests use platform detection and `Assert.Ignore` when the required tool or OS is not available, so they never fail due to missing prerequisites.

---

## Architecture

### Format Registry (auto-discovery)

Adding a new format requires only:

1. Create a `FileFormat.*` project with an `IFormatDescriptor` class
2. Add a `ProjectReference` to `Compression.Lib.csproj`
3. Add the project to `CompressionWorkbench.slnx`

A Roslyn source generator discovers all descriptors at compile time and generates registration code - no reflection, no manual switch statements, no hardcoded lists. The same mechanism discovers `IBuildingBlock` implementations in Compression.Core.

### Principles

1. **No external compression code** - Every algorithm is implemented from scratch in C#
2. **Composable primitives** - `Compression.Core` provides building blocks; `FileFormat.*` projects compose them
3. **Stream-oriented** - All compression/decompression operates on `System.IO.Stream`
4. **Immutable headers** - File format header structures are immutable record types
5. **Testability** - Every component is independently testable
6. **.NET 10 / C# 14** - Latest language features, nullable reference types, warnings-as-errors

---

## Building

```bash
dotnet build CompressionWorkbench.slnx
```

## Testing

```bash
dotnet test
```

---

## References to learn from

- **RFCs**: [RFC 1951](https://www.rfc-editor.org/rfc/rfc1951) (Deflate), [RFC 1952](https://www.rfc-editor.org/rfc/rfc1952) (Gzip), [RFC 1950](https://www.rfc-editor.org/rfc/rfc1950) (Zlib), [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932) (Brotli)
- **[libxad](https://github.com/ashang/libxad)** - The eXternal Archive Decompressor, format documentation reference
- **[XADMaster / The Unarchiver](https://github.com/MacPaw/XADMaster)** - Modern continuation of libxad
- **[libarchive](https://github.com/libarchive/libarchive)** - Multi-format reference
- **[Wikipedia](https://en.wikipedia.org/wiki/List_of_archive_formats)** - List of known formats
- **[ArchiveTeam](http://justsolve.archiveteam.org/wiki/Compression)** - Compression format documentation
- **[7-Zip](https://github.com/ip7z/7zip)** - Multi-Archiver
