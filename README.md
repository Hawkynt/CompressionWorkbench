# CompressionWorkbench

![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)
![Language](https://img.shields.io/github/languages/top/Hawkynt/CompressionWorkbench?color=purple)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/CompressionWorkbench?branch=main)![Activity](https://img.shields.io/github/commit-activity/y/Hawkynt/CompressionWorkbench?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/commits/main)
[![GitHub release](https://img.shields.io/github/v/release/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Hawkynt/CompressionWorkbench/total)](https://github.com/Hawkynt/CompressionWorkbench/releases)
[![Build](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/Build.yml/badge.svg)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/Build.yml)

> A fully clean-room C# implementation of compression primitives, archive file formats, and analysis tools. Every algorithm is implemented from scratch using no external compression source code - only our own primitives.

---

## Vision

CompressionWorkbench is both a **toolkit** and a **learning platform** for data compression. It aims to:

1. **Implement every major compression algorithm from scratch** - no wrappers around zlib, liblzma, or other native libraries
2. **Support every common archive format** - read, write, convert, and optimize across different-era formats
3. **Provide a binary analysis engine** - identify unknown compressed data, map entropy, fingerprint algorithms, and reconstruct compression chains
4. **Benchmark compression building blocks** - compare raw algorithm performance (ratio, speed) across data patterns
5. **Offer multiple interfaces** - GUI, CLI tool, Explorer shell integration, and self-extracting archives

---

## Domains

| Domain | Scope |
|--------|-------|
| **Primitives** | Bit I/O, Huffman, arithmetic/range coding, ANS/FSE, LZ family, BWT, MTF, PPM, context mixing |
| **Formats** | Archive containers (ZIP, 7z, RAR, ...) and compression streams (Gzip, Brotli, Zstd, ...) |
| **Analysis** | Signature scanning, algorithm fingerprinting, entropy mapping, trial decompression, chain reconstruction |
| **Benchmarking** | Compare building blocks (raw algorithms without file format overhead) across data patterns |
| **Tools** | CLI archiver (`cwb`), WPF browser, Explorer shell integration, SFX stubs |

---

## Technology Stack

| Concern       | Choice                                      |
|---------------|---------------------------------------------|
| Language      | C# 14 / .NET 10                             |
| Solution      | `.slnx` (XML solution format)               |
| Testing       | NUnit                                       |
| GUI           | WPF (`Compression.UI`)                      |
| Shell         | Explorer context menu (`Compression.Shell`) |
| CLI           | System.CommandLine v3 (`Compression.CLI`)   |
| Analysis      | `Compression.Analysis` library              |
| SFX           | `Compression.Sfx.Cli` / `Compression.Sfx.Ui` stubs |
| Discovery     | Roslyn source generator (zero-reflection format/block registration) |

---

## Solution Structure

```
CompressionWorkbench.slnx
│
├── Compression.Core              Core primitives, building blocks, and algorithms
├── Compression.Registry          Interfaces (IFormatDescriptor, IBuildingBlock), registries
├── Compression.Registry.Generator  Roslyn source generator for auto-discovery
├── Compression.Lib               Umbrella library: format detection, archive operations, SFX
├── Compression.Analysis          Binary analysis engine (signatures, entropy, fingerprinting)
├── Compression.CLI               Universal command-line archive tool (cwb)
├── Compression.UI                WPF archive browser & analysis wizard
├── Compression.Shell             Explorer context menu integration
├── Compression.Sfx.Cli           Self-extracting archive stub (console)
├── Compression.Sfx.Ui            Self-extracting archive stub (GUI)
├── Compression.Tests             NUnit test project
│
├── FileFormat.Zip                ZIP archive format
├── FileFormat.Rar                RAR archive format (v1-v5, read + v4/v5 write)
├── FileFormat.SevenZip           7z archive format
├── FileFormat.Gzip               GZIP format
├── FileFormat.Bzip2              BZIP2 format
├── FileFormat.Xz                 XZ format
├── FileFormat.Zstd               Zstandard format
├── FileFormat.Tar                TAR container format (POSIX/GNU/PAX, multi-volume)
├── FileFormat.Arj                ARJ archive format
├── FileFormat.Cab                Microsoft Cabinet format (MSZIP/LZX/Quantum)
├── FileFormat.Lzh                LZH/LHA archive format (lh0-lh7, lzs, pm0-pm2)
├── FileFormat.Zoo                ZOO archive format
├── FileFormat.Arc                ARC archive format
├── FileFormat.Ace                ACE archive format (v1/v2, sound/picture filters)
├── FileFormat.Sqx                SQX archive format (LZH/multimedia/audio codecs)
├── FileFormat.Cpio               CPIO archive format
├── FileFormat.Ar                 Unix AR archive format
├── FileFormat.Wim                WIM image format (LZX/XPRESS)
├── FileFormat.Rpm                RPM package format
├── FileFormat.Deb                Debian package format
├── FileFormat.Shar               Shell archive format
├── FileFormat.Pak                PAK archive format
├── FileFormat.Ha                 HA archive format (HSC/ASC)
├── FileFormat.Zpaq               ZPAQ archive format
├── FileFormat.StuffIt            StuffIt archive format
├── FileFormat.SquashFs            SquashFS filesystem image
├── FileFormat.CramFs             CramFS filesystem image
├── FileFormat.Nsis               NSIS installer format
├── FileFormat.InnoSetup          Inno Setup installer format
├── FileFormat.Wad                Doom WAD format
├── FileFormat.Xar                XAR (eXtensible ARchive) format
├── FileFormat.AlZip              ALZip (.alz) Korean archive format
├── FileFormat.Vpk                Valve VPK game archive format
├── FileFormat.Bsa                Bethesda BSA/BA2 game archive format
├── FileFormat.Mpq                Blizzard MPQ game archive format
│
├── FileFormat.Lz4                LZ4 block + frame format
├── FileFormat.Brotli             Brotli format
├── FileFormat.Snappy             Snappy format
├── FileFormat.Lzop               LZOP format
├── FileFormat.Compress           Unix .Z format (LZW)
├── FileFormat.Lzma               LZMA alone format
├── FileFormat.Lzip               Lzip format
├── FileFormat.Zlib               Zlib format (RFC 1950)
├── FileFormat.Szdd               MS-DOS COMPRESS.EXE LZSS format
├── FileFormat.Kwaj               MS-DOS COMPRESS.EXE extended format
├── FileFormat.Rzip               RZIP format
├── FileFormat.MacBinary          MacBinary format
├── FileFormat.BinHex             BinHex (.hqx) format
├── FileFormat.Squeeze            Squeeze (.sqz) format
├── FileFormat.PackBits           PackBits RLE format
├── FileFormat.Yaz0               Nintendo Yaz0 (SZS) format
├── FileFormat.BriefLz            BriefLZ format
├── FileFormat.Rnc                Rob Northen Compression (RNC) format
├── FileFormat.RefPack            RefPack/QFS (EA) format
├── FileFormat.ApLib               aPLib compression format
├── FileFormat.Lzfse              Apple LZFSE/LZVN format
├── FileFormat.Freeze             Freeze (Unix) format
├── FileFormat.UuEncoding         UuEncoding format
├── FileFormat.YEnc               yEnc binary-to-text encoding
├── FileFormat.Density            Density (Centaurean) format
├── FileFormat.Lzg                LZG format
├── FileFormat.Bcm                BCM (BWT + arithmetic) format
├── FileFormat.Bsc                BSC (block-sorting compressor) format
├── FileFormat.Balz               BALZ (ROLZ + arithmetic) format
├── FileFormat.Csc                CSC (context-based) format
├── FileFormat.Zling              Zling (ROLZ + Huffman) format
├── FileFormat.Lizard             Lizard (LZ5) format
├── FileFormat.QuickLz            QuickLZ format
├── FileFormat.Cmix               cmix (neural network) format
├── FileFormat.Mcm                MCM (context mixing) format
├── FileFormat.Paq8               PAQ8 (context mixing) format
│
├── FileFormat.Dms                Amiga DMS format
├── FileFormat.Lzx                Amiga LZX format
├── FileFormat.PowerPacker        Amiga PowerPacker (PP20) format
├── FileFormat.CompactPro         Classic Mac Compact Pro format
├── FileFormat.Spark              RISC OS Spark format
├── FileFormat.Lbr                CP/M LBR format
├── FileFormat.IcePacker          Atari ST ICE Packer format
└── FileFormat.Uharc              UHARC format
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
- **Codecs**: RLE, Golomb-Rice, ACE LZ77+Huffman, SQX LZH, Brotli LZ77
- **Data Structures**: Sliding window, priority queue, trie, suffix tree

### Building Blocks

Building blocks are raw algorithm primitives without file format overhead, registered via `IBuildingBlock`:

| Family | Algorithms |
|--------|-----------|
| **Dictionary** | DEFLATE, Deflate64, LZ77, LZ78, LZW, LZO1X, LZSS, LZ4, Snappy, Brotli, LZMA, LZX, XPRESS Huffman, LZH, ARJ, LZMS, LZP, ACE, RAR5, SQX, ROLZ, PPM, CTW |
| **Entropy** | Huffman, Arithmetic, Shannon-Fano, Golomb/Rice, Fibonacci, FSE/tANS, BPE, Range Coding |
| **Transform** | BWT, MTF, Delta, RLE |

These are the algorithms compared in the benchmark tool - they operate on raw `byte[]` data with no container format.

---

## Supported Formats

### Archive Formats

| Format | Extensions | Read | Write | Methods |
|--------|-----------|------|-------|---------|
| ZIP | `.zip` | Yes | Yes | Store, Deflate, Deflate64, Shrink, Reduce, Implode, BZip2, LZMA, PPMd, Zstd, AES |
| RAR | `.rar` | Yes | Yes (v4/v5) | RAR v1-v5 decoders, solid, multi-volume, encryption, recovery |
| 7z | `.7z` | Yes | Yes | LZMA/LZMA2, Deflate, BZip2, PPMd, BCJ/BCJ2, AES-256, multi-volume |
| TAR | `.tar` | Yes | Yes | POSIX/GNU/PAX, multi-volume |
| CAB | `.cab` | Yes | Yes | MSZIP, LZX, Quantum |
| LZH/LHA | `.lzh`,`.lha` | Yes | Yes | lh0-lh7, lzs, lh1-lh3 (adaptive Huffman), pm0-pm2 |
| ARJ | `.arj` | Yes | Yes | Methods 0-4, garble encryption |
| ARC | `.arc` | Yes | Yes | Methods 0-9 (RLE, LZW, Squeeze, Huffman) |
| ZOO | `.zoo` | Yes | Yes | LZW, LZH |
| ACE | `.ace` | Yes | Yes | ACE 1.0/2.0, solid, sound/picture filters, Blowfish encryption, recovery |
| SQX | `.sqx` | Yes | Yes | LZH, multimedia, audio, solid, AES-128, recovery |
| CPIO | `.cpio` | Yes | Yes | Binary, odc, newc, CRC |
| AR | `.ar` | Yes | Yes | Unix archive |
| WIM | `.wim` | Yes | Yes | LZX, XPRESS |
| RPM | `.rpm` | Yes | Yes | CPIO payload |
| DEB | `.deb` | Yes | Yes | AR+TAR with gz/xz/zst/bz2 |
| Shar | `.shar` | Yes | Yes | Shell archive |
| PAK | `.pak` | Yes | Yes | ARC-compatible |
| HA | `.ha` | Yes | Yes | HSC/ASC arithmetic coding |
| ZPAQ | `.zpaq` | Yes | Yes | Context mixing, journaling |
| StuffIt | `.sit` | Yes | Yes | Multiple methods |
| SquashFS | `.sqfs` | Yes | Yes | Filesystem image |
| CramFS | `.cramfs` | Yes | Yes | Filesystem image |
| NSIS | `.exe` | Yes | - | Installer extraction |
| Inno Setup | `.exe` | Yes | - | Installer extraction |
| DMS | `.dms` | Yes | Yes | Amiga disk archiver |
| LZX (Amiga) | `.lzx` | Yes | Yes | Amiga LZX format |
| Compact Pro | `.cpt` | Yes | Yes | Classic Mac format |
| Spark | `.spark` | Yes | Yes | RISC OS format |
| LBR | `.lbr` | Yes | Yes | CP/M format |
| UHARC | `.uha` | Yes | Yes | LZP compression |
| WAD | `.wad` | Yes | Yes | Doom WAD format |
| XAR | `.xar` | Yes | Yes | Apple .pkg format (zlib-compressed TOC) |
| ALZip | `.alz` | Yes | Yes | Korean archive (Deflate) |
| VPK | `.vpk` | Yes | Yes | Valve game archive |
| BSA | `.bsa`,`.ba2` | Yes | Yes | Bethesda game archive |
| MPQ | `.mpq` | Yes | Yes | Blizzard game archive |

### Compression Stream Formats

| Format | Extensions | Compress | Decompress |
|--------|-----------|----------|------------|
| Gzip | `.gz` | Yes | Yes |
| BZip2 | `.bz2` | Yes | Yes |
| XZ | `.xz` | Yes | Yes |
| Zstandard | `.zst` | Yes | Yes |
| LZ4 | `.lz4` | Yes | Yes |
| Brotli | `.br` | Yes | Yes |
| Snappy | `.sz`,`.snappy` | Yes | Yes |
| LZOP | `.lzo` | Yes | Yes |
| Compress | `.Z` | Yes | Yes |
| LZMA | `.lzma` | Yes | Yes |
| Lzip | `.lz` | Yes | Yes |
| Zlib | `.zlib` | Yes | Yes |
| SZDD | `.sz_` | Yes | Yes |
| KWAJ | - | Yes | Yes |
| RZIP | `.rz` | Yes | Yes |
| MacBinary | `.bin` | Yes | Yes |
| BinHex | `.hqx` | Yes | Yes |
| Squeeze | `.sqz` | Yes | Yes |
| PowerPacker | `.pp` | Yes | Yes |
| ICE Packer | `.ice` | Yes | Yes |
| PackBits | `.packbits` | Yes | Yes |
| Yaz0 | `.yaz0`,`.szs` | Yes | Yes |
| BriefLZ | `.blz` | Yes | Yes |
| RNC | `.rnc` | Yes | Yes |
| RefPack | `.qfs`,`.refpack` | Yes | Yes |
| aPLib | `.aplib` | Yes | Yes |
| LZFSE | `.lzfse` | Yes | Yes |
| Freeze | `.f`,`.freeze` | Yes | Yes |
| UuEncoding | `.uu`,`.uue` | Yes | Yes |
| yEnc | `.yenc` | Yes | Yes |
| Density | `.density` | Yes | Yes |
| LZG | `.lzg` | Yes | Yes |
| BCM | `.bcm` | Yes | Yes |
| BSC | `.bsc` | Yes | Yes |
| BALZ | `.balz` | Yes | Yes |
| CSC | `.csc` | Yes | Yes |
| Zling | `.zling` | Yes | Yes |
| Lizard | `.lizard` | Yes | Yes |
| QuickLZ | `.quicklz` | Yes | Yes |
| cmix | `.cmix` | Yes | Yes |
| MCM | `.mcm` | Yes | Yes |
| PAQ8 | `.paq8` | Yes | Yes |

### Compound Formats

`tar.gz`, `tar.bz2`, `tar.xz`, `tar.zst`, `tar.lz4`, `tar.lz`, `tar.br`

### Detection-Only

ISO 9660, UDF

---

## Compression.Analysis - Binary Analysis Engine

The analysis engine provides tools for identifying and characterizing unknown binary data:

- **Signature Scanner**: Magic bytes detection for all registered format signatures (auto-discovered from FormatRegistry)
- **Algorithm Fingerprinting**: Statistical fingerprinting to identify compression algorithms
- **Trial Decompression**: Attempts all registered stream decompressors to find valid streams
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

---

## Compression.CLI - `cwb` Command-Line Tool

A universal archive tool with smart conversion and optimal re-encoding.

### Commands

| Command | Alias | Description |
|---------|-------|-------------|
| `list <archive>` | `l` | List contents of an archive |
| `extract <archive> [files...]` | `x` | Extract files from an archive |
| `create <archive> <files...>` | `c` | Create a new archive |
| `test <archive>` | `t` | Test archive integrity |
| `info <archive>` | - | Show detailed archive information |
| `convert <input> <output>` | - | Convert between archive formats |
| `optimize <input> <output>` | `opt` | Re-encode with optimal compression |
| `benchmark <file>` | `bench` | Benchmark all building blocks on a file |
| `analyze <file>` | - | Run binary analysis |
| `formats` | - | List all supported formats |

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

| Tier | Strategy | Example |
|------|----------|---------|
| 1 | Bitstream transfer (zero decompression) | `gz` <-> `zlib`, `zip` <-> `gz` |
| 2 | Container restream (decompress wrapper only) | `tar.gz` -> `tar.xz` |
| 3 | Full recompress (extract + re-encode) | `zip` -> `7z` |

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
- 