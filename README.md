# CompressionWorkbench

![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)
![Language](https://img.shields.io/github/languages/top/Hawkynt/CompressionWorkbench?color=purple)
[![Last Commit](https://img.shields.io/github/last-commit/Hawkynt/CompressionWorkbench?branch=main)![Activity](https://img.shields.io/github/commit-activity/y/Hawkynt/CompressionWorkbench?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/commits/main)
[![GitHub release](https://img.shields.io/github/v/release/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Hawkynt/CompressionWorkbench/total)](https://github.com/Hawkynt/CompressionWorkbench/releases)
[![Build](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/Build.yml/badge.svg)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/Build.yml)

> A fully clean-room C# implementation of compression primitives, archive file formats, and analysis tools. Every algorithm is implemented from scratch using no external compression source code — only our own primitives. Licensing and patent concerns are not applicable as all code is original.

---

## Product Requirements Document (PRD)

### Vision

Build a comprehensive compression toolkit in C# that implements all major (and exotic) compression algorithms and archive formats from first principles. The project spans from low-level bit manipulation and entropy coding up through complete archive format read/write support, a CLI tool, a WinForms archiver, and an innovative file format analyzer wizard.

### Solution Structure

```
CompressionWorkbench.slnx
│
│   ├── Compression.Core              Core primitives and algorithms
│   ├── FileFormat.Zip                ZIP archive format
│   ├── FileFormat.Rar                RAR archive format (v1–v5)
│   ├── FileFormat.SevenZip           7z archive format
│   ├── FileFormat.Gzip               GZIP format
│   ├── FileFormat.Bzip2              BZIP2 format
│   ├── FileFormat.Xz                 XZ format
│   ├── FileFormat.Zstd               Zstandard format
│   ├── FileFormat.Tar                TAR container format
│   ├── FileFormat.Arj                ARJ archive format
│   ├── FileFormat.Cab                Microsoft Cabinet format
│   ├── FileFormat.Lzh                LZH/LHA archive format
│   ├── FileFormat.Zoo                ZOO archive format
│   ├── FileFormat.Arc                ARC archive format
│   ├── FileFormat.Ace                ACE archive format
│   ├── FileFormat.Sqx                SQX archive format
│   ├── FileFormat.Pak                PAK archive format
│   ├── FileFormat.Ha                 HA archive format
│   ├── FileFormat.Zpaq               ZPAQ archive format
│   ├── FileFormat.Lz4                LZ4 format
│   ├── FileFormat.Brotli             Brotli format
│   ├── FileFormat.Snappy             Snappy format
│   ├── FileFormat.Rzip               RZIP format
│   ├── FileFormat.Cpio               CPIO archive format
│   ├── FileFormat.Shar               Shell archive format
│   ├── FileFormat.Lzop               LZOP format
│   ├── FileFormat.Compress           Unix compress (.Z) format
│   ├── FileFormat.Stuffit            StuffIt (.sit/.sitx) format
│   ├── FileFormat.Dms                Amiga DMS format
│   ├── FileFormat.Alz                ALZ archive format
│   ├── FileFormat.Egg                EGG archive format
│   ├── FileFormat.Wim                WIM archive format
│   ├── FileFormat.Kgb                KGB archive format
│   ├── FileFormat.Pea                PEA archive format
│   ├── FileFormat.Uha                UHA archive format
│   ├── Compression.CLI               Command-line archiving tool
│   ├── Compression.WinForms          WinForms GUI archiver
│   └── Compression.Analyzer.WinForms Guided analysis wizard
    └── Compression.Tests             NUnit test project
```

### Technology Stack

| Concern       | Choice                          |
|---------------|---------------------------------|
| Language      | C# / .NET 9+                   |
| Solution      | `.slnx` (XML solution format)  |
| Testing       | NUnit                           |
| GUI           | WinForms                        |
| CLI           | System.CommandLine              |
| CI            | GitHub Actions (planned)        |

---

## MoSCoW Prioritization

### Must Have — Modern Formats

These are the most widely used formats today and form the core deliverable.

| Format   | Extensions            | Compression Methods                        |
|----------|-----------------------|--------------------------------------------|
| ZIP      | `.zip`                | Store, Deflate, Deflate64, LZMA, BZip2, PPMd |
| RAR      | `.rar`                | RAR v1, v2, v3, v4, v5                     |
| 7z       | `.7z`                 | LZMA, LZMA2, BCJ, BCJ2, PPMd, BZip2, Deflate |
| GZIP     | `.gz`                 | Deflate                                    |
| TAR      | `.tar`, `.tar.*`      | Container only (pairs with gz/bz2/xz/zst) |
| BZIP2    | `.bz2`                | BWT + MTF + Huffman                        |
| XZ       | `.xz`                 | LZMA2                                      |
| Zstandard| `.zst`                | Zstandard (FSE + Huffman + LZ)             |

### Should Have — Older/Legacy Formats

Classic formats from the DOS/early Windows era, still encountered in archives.

| Format   | Extensions            | Compression Methods                        |
|----------|-----------------------|--------------------------------------------|
| ARJ      | `.arj`                | LZW, Huffman variants                      |
| CAB      | `.cab`                | MSZIP, LZX, Quantum                        |
| LZH/LHA  | `.lzh`, `.lha`       | LZSS + Huffman (lh0–lh7)                  |
| ZOO      | `.zoo`                | LZW, LZH                                  |
| ARC      | `.arc`                | RLE, LZW, Huffman, Squashing               |
| CPIO     | `.cpio`               | Container format                           |
| Compress | `.Z`                  | LZW (Unix compress)                        |
| LZOP     | `.lzo`                | LZO1X                                      |

### Could Have — Exotic/Niche Formats

Uncommon or platform-specific formats for completeness.

| Format    | Extensions           | Notes                                      |
|-----------|----------------------|--------------------------------------------|
| ACE       | `.ace`               | ACE v1/v2, proprietary LZ+Huffman          |
| SQX       | `.sqx`               | Multi-algorithm (LZA, LZW, PPM, BWT)       |
| PAK       | `.pak`               | ARC-compatible variant                      |
| HA        | `.ha`                | HSC/ASC arithmetic coding                   |
| ZPAQ      | `.zpaq`              | Context-mixing, journaling                  |
| LZ4       | `.lz4`               | LZ4 block/frame format                     |
| Brotli    | `.br`                | LZ77 + Huffman + 2nd-order context modeling |
| Snappy    | `.sz`, `.snappy`     | LZ77 variant (speed-optimized)              |
| RZIP      | `.rz`                | Long-range redundancy + bzip2               |
| StuffIt   | `.sit`, `.sitx`      | Mac-classic, multiple methods               |
| DMS       | `.dms`               | Amiga disk archiver                         |
| ALZ       | `.alz`               | Korean archiver (ALZip)                     |
| EGG       | `.egg`               | Korean archiver (ALZip successor)           |
| WIM       | `.wim`               | Windows Imaging, LZX/XPRESS                |
| KGB       | `.kgb`               | PAQ-family, ultra-high compression          |
| PEA       | `.pea`               | Multi-algorithm, authenticated encryption   |
| UHA       | `.uha`               | UHBC, very high ratio                       |
| Shar      | `.shar`              | Shell archive (self-extracting)             |

### Won't Have

These are explicitly out of scope:

- **ISO 9660** — Optical disc filesystem images
- **FAT/NTFS** — Filesystem images embedded inside files
- **VHD/VMDK/VDI** — Virtual disk images
- **SquashFS/CramFS/JFFS2** — Embedded/Linux filesystem images
- **ROM/firmware images** — Console/device-specific formats

---

## Compression.Core — Primitives & Algorithms

The heart of the project. Every `FileFormat.*` project builds on these primitives.

### Bit-Level I/O

- [ ] `BitReader` — LSB-first and MSB-first bit reading from streams
- [ ] `BitWriter` — LSB-first and MSB-first bit writing to streams
- [ ] `BitBuffer` — Buffered bit-level I/O with lookahead

### Entropy Coding

- [ ] **Huffman Coding** — Static and dynamic Huffman trees, canonical Huffman codes
- [ ] **Arithmetic Coding** — Range coder, binary arithmetic coder
- [ ] **ANS / FSE** — Asymmetric Numeral Systems / Finite State Entropy (for Zstandard)
- [ ] **Range Coding** — Byte-aligned arithmetic coding variant

### Dictionary Compression (LZ Family)

- [ ] **LZ77** — Sliding window, match finder (hash chain, binary tree, suffix array)
- [ ] **LZSS** — LZ77 variant with flag bits (used by LZH, Deflate)
- [ ] **LZ78** — Dictionary-based phrase encoding
- [ ] **LZW** — LZ78 with implicit dictionary (used by ARC, ZOO, Compress, GIF)
- [ ] **LZMA** — Markov-chain range-coded LZ with sophisticated match finding
- [ ] **LZMA2** — Chunked LZMA with uncompressed block support
- [ ] **LZO** — Speed-optimized LZ77 variant
- [ ] **LZ4** — Extremely fast LZ77 variant
- [ ] **LZX** — LZ77 + Huffman with sliding window (used by CAB, WIM)
- [ ] **Zstandard engine** — FSE + Huffman + LZ sequence coding

### Transform Coding

- [ ] **BWT** — Burrows-Wheeler Transform (forward and inverse)
- [ ] **MTF** — Move-To-Front transform
- [ ] **Delta Encoding** — Byte-level delta filters (delta1, delta2, delta4)
- [ ] **BCJ / BCJ2** — Branch/Call/Jump filters for x86, ARM, ARMT, PPC, SPARC, IA64

### Prediction & Context Modeling

- [ ] **PPMd** — Prediction by Partial Matching (variants: PPMd model H, model I)
- [ ] **Context Mixing** — PAQ-family weighted context mixing (for KGB/ZPAQ)
- [ ] **Order-0 / Order-N models** — Statistical byte models

### Checksums & Hashing

- [ ] **CRC-16** — 16-bit cyclic redundancy check
- [ ] **CRC-32** — Standard CRC-32 and CRC-32C (Castagnoli)
- [ ] **Adler-32** — Adler checksum (used by zlib)
- [ ] **SHA-256** — For integrity verification
- [ ] **AES-256** — For encrypted archive support (ZIP, RAR, 7z)
- [ ] **BLAKE2** — For modern archive integrity

### Match Finders

- [ ] **Hash Chain** — Fast, memory-efficient match finding
- [ ] **Binary Tree** — Balanced match finding for better ratios
- [ ] **Hash Array (HC3/HC4/BT2/BT3/BT4)** — LZMA-style match finders
- [ ] **Suffix Array** — Optimal parsing match finding
- [ ] **Optimal Parser** — Price-based optimal LZ parsing

### Data Structures

- [ ] **Sliding Window** — Configurable-size circular buffer
- [ ] **Priority Queue** — For Huffman tree construction
- [ ] **Trie** — For LZW dictionary
- [ ] **Suffix Tree** — For advanced match finding

### Shared Infrastructure

- [ ] **Stream abstractions** — Composable compression/decompression streams
- [ ] **Progress reporting** — Unified progress callback interface
- [ ] **Parallel compression** — Block-level parallelism support
- [ ] **Memory management** — Pooled buffers, memory-limited operation

---

## User Stories

### Epic 1: Project Foundation

- [ ] **US-1.1** As a developer, I can open `CompressionWorkbench.slnx` in Visual Studio / Rider and build all projects successfully
- [ ] **US-1.2** As a developer, I can run `dotnet test` and see all unit tests discovered and passing
- [ ] **US-1.3** As a developer, I can reference `Compression.Core` from any `FileFormat.*` project and access all primitives
- [ ] **US-1.4** As a developer, I can see XML doc comments on all public APIs in `Compression.Core`

### Epic 2: Bit-Level I/O

- [ ] **US-2.1** As a developer, I can read individual bits from a stream in both LSB-first and MSB-first order
- [ ] **US-2.2** As a developer, I can write individual bits to a stream in both LSB-first and MSB-first order
- [ ] **US-2.3** As a developer, I can read/write N-bit integers (1–32 bits) from/to streams
- [ ] **US-2.4** As a developer, I can peek at upcoming bits without consuming them (lookahead)

### Epic 3: Huffman Coding

- [ ] **US-3.1** As a developer, I can build a Huffman tree from frequency counts
- [ ] **US-3.2** As a developer, I can encode data using a static Huffman table
- [ ] **US-3.3** As a developer, I can decode data using a static Huffman table
- [ ] **US-3.4** As a developer, I can build and use canonical Huffman codes (as used by Deflate, etc.)
- [ ] **US-3.5** As a developer, I can serialize/deserialize Huffman code length tables
- [ ] **US-3.6** As a developer, round-tripping arbitrary data through Huffman encode/decode produces identical output

### Epic 4: Arithmetic / Range Coding

- [ ] **US-4.1** As a developer, I can encode a symbol sequence using arithmetic coding with a given probability model
- [ ] **US-4.2** As a developer, I can decode an arithmetic-coded stream given the same model
- [ ] **US-4.3** As a developer, I can use a range coder (byte-aligned arithmetic coder) for LZMA-style coding
- [ ] **US-4.4** As a developer, I can use adaptive probability models that update during encode/decode

### Epic 5: LZ77 / LZSS

- [ ] **US-5.1** As a developer, I can compress data using LZ77 with a configurable sliding window size
- [ ] **US-5.2** As a developer, I can decompress LZ77-encoded data
- [ ] **US-5.3** As a developer, I can find matches using a hash-chain match finder
- [ ] **US-5.4** As a developer, I can find matches using a binary-tree match finder
- [ ] **US-5.5** As a developer, I can use LZSS encoding (flag bits indicating literal vs match)
- [ ] **US-5.6** As a developer, round-tripping arbitrary data through LZ77/LZSS produces identical output

### Epic 6: LZW

- [ ] **US-6.1** As a developer, I can compress data using LZW with configurable max dictionary size
- [ ] **US-6.2** As a developer, I can decompress LZW-encoded data
- [ ] **US-6.3** As a developer, I can handle the LZW "KwKwK" edge case correctly
- [ ] **US-6.4** As a developer, I can use variable-width codes (9–16 bits) as used by Unix compress

### Epic 7: Deflate

- [ ] **US-7.1** As a developer, I can decompress a raw Deflate stream (RFC 1951)
- [ ] **US-7.2** As a developer, I can compress data into a valid raw Deflate stream
- [ ] **US-7.3** As a developer, I can handle all three Deflate block types: uncompressed, static Huffman, dynamic Huffman
- [ ] **US-7.4** As a developer, I can compress with configurable levels (1=fast through 9=best)
- [ ] **US-7.5** As a developer, Deflate64 (enhanced Deflate) is supported for decompression and compression

### Epic 8: LZMA / LZMA2

- [ ] **US-8.1** As a developer, I can decompress an LZMA-encoded stream
- [ ] **US-8.2** As a developer, I can compress data into a valid LZMA stream
- [ ] **US-8.3** As a developer, I can use the LZMA range coder with its probability model
- [ ] **US-8.4** As a developer, I can decompress/compress LZMA2 chunked streams
- [ ] **US-8.5** As a developer, I can configure LZMA dictionary size, word size, and match finder

### Epic 9: BWT / Bzip2 Engine

- [ ] **US-9.1** As a developer, I can compute the Burrows-Wheeler Transform of a block
- [ ] **US-9.2** As a developer, I can compute the inverse BWT
- [ ] **US-9.3** As a developer, I can apply Move-To-Front encoding/decoding
- [ ] **US-9.4** As a developer, I can compress data using the full bzip2 pipeline: RLE1 → BWT → MTF → RLE2 → Huffman
- [ ] **US-9.5** As a developer, I can decompress bzip2-compressed data

### Epic 10: PPMd

- [ ] **US-10.1** As a developer, I can compress/decompress using PPMd Model H (as used by 7z)
- [ ] **US-10.2** As a developer, I can compress/decompress using PPMd Model I (as used by RAR)
- [ ] **US-10.3** As a developer, I can configure PPMd model order and memory size

### Epic 11: ANS / FSE / Zstandard Engine

- [ ] **US-11.1** As a developer, I can encode/decode using tANS (tabled ANS)
- [ ] **US-11.2** As a developer, I can encode/decode using FSE (Finite State Entropy)
- [ ] **US-11.3** As a developer, I can compress/decompress using the full Zstandard pipeline
- [ ] **US-11.4** As a developer, I can handle Zstandard frames, blocks, sequences, and dictionaries

### Epic 12: Filters & Transforms

- [ ] **US-12.1** As a developer, I can apply/reverse BCJ (x86) filters
- [ ] **US-12.2** As a developer, I can apply/reverse BCJ2 filters
- [ ] **US-12.3** As a developer, I can apply/reverse ARM, ARMT, PPC, SPARC, IA64 filters
- [ ] **US-12.4** As a developer, I can apply delta encoding/decoding with configurable byte distance

### Epic 13: Checksums & Crypto

- [ ] **US-13.1** As a developer, I can compute CRC-32 checksums matching standard implementations
- [ ] **US-13.2** As a developer, I can compute CRC-16 and Adler-32 checksums
- [ ] **US-13.3** As a developer, I can compute SHA-256 hashes for integrity verification
- [ ] **US-13.4** As a developer, I can encrypt/decrypt using AES-256-CBC and AES-256-CTR
- [ ] **US-13.5** As a developer, I can derive encryption keys using PBKDF2/scrypt as used by ZIP/RAR/7z

---

### Epic 14: FileFormat.Zip (Must Have)

- [ ] **US-14.1** As a user, I can list the contents of a ZIP archive
- [ ] **US-14.2** As a user, I can extract files from a ZIP archive (Store, Deflate methods)
- [ ] **US-14.3** As a user, I can create a new ZIP archive with Deflate compression
- [ ] **US-14.4** As a developer, I can read/write all ZIP structures: local file header, central directory, EOCD, ZIP64
- [ ] **US-14.5** As a user, I can handle ZIP files with Deflate64, LZMA, BZip2, and PPMd methods
- [ ] **US-14.6** As a user, I can extract/create password-protected ZIP files (ZipCrypto and AES)
- [ ] **US-14.7** As a user, I can handle ZIP files larger than 4 GB (ZIP64)
- [ ] **US-14.8** As a user, I can handle multi-volume/split ZIP archives

### Epic 15: FileFormat.Gzip (Must Have)

- [ ] **US-15.1** As a user, I can decompress `.gz` files
- [ ] **US-15.2** As a user, I can compress files into `.gz` format
- [ ] **US-15.3** As a developer, I can read/write GZIP headers (RFC 1952) including metadata
- [ ] **US-15.4** As a user, I can handle multi-member GZIP files

### Epic 16: FileFormat.Bzip2 (Must Have)

- [ ] **US-16.1** As a user, I can decompress `.bz2` files
- [ ] **US-16.2** As a user, I can compress files into `.bz2` format
- [ ] **US-16.3** As a developer, I can read/write bzip2 stream headers and block structure

### Epic 17: FileFormat.Tar (Must Have)

- [ ] **US-17.1** As a user, I can list and extract files from TAR archives (POSIX, GNU, UStar formats)
- [ ] **US-17.2** As a user, I can create TAR archives
- [ ] **US-17.3** As a user, I can handle `.tar.gz`, `.tar.bz2`, `.tar.xz`, `.tar.zst` compound formats
- [ ] **US-17.4** As a developer, I can handle long file names (GNU and PAX extensions)

### Epic 18: FileFormat.Xz (Must Have)

- [ ] **US-18.1** As a user, I can decompress `.xz` files
- [ ] **US-18.2** As a user, I can compress files into `.xz` format
- [ ] **US-18.3** As a developer, I can read/write XZ stream headers, block headers, and index
- [ ] **US-18.4** As a developer, I can handle XZ filters (LZMA2, BCJ, delta)

### Epic 19: FileFormat.SevenZip (Must Have)

- [ ] **US-19.1** As a user, I can list the contents of a 7z archive
- [ ] **US-19.2** As a user, I can extract files from a 7z archive
- [ ] **US-19.3** As a user, I can create 7z archives with LZMA2 compression
- [ ] **US-19.4** As a developer, I can read/write 7z header structures (SignatureHeader, PackInfo, CodersInfo, etc.)
- [ ] **US-19.5** As a user, I can handle 7z solid archives
- [ ] **US-19.6** As a user, I can handle 7z with multiple coders/filters chained
- [ ] **US-19.7** As a user, I can extract/create password-protected 7z files (AES-256)

### Epic 20: FileFormat.Zstd (Must Have)

- [ ] **US-20.1** As a user, I can decompress `.zst` files
- [ ] **US-20.2** As a user, I can compress files into `.zst` format
- [ ] **US-20.3** As a developer, I can read/write Zstandard frames and blocks
- [ ] **US-20.4** As a developer, I can handle Zstandard dictionaries

### Epic 21: FileFormat.Rar (Must Have)

- [ ] **US-21.1** As a user, I can list the contents of a RAR archive (v4 and v5)
- [ ] **US-21.2** As a user, I can extract files from RAR archives (v4 and v5)
- [ ] **US-21.3** As a user, I can create RAR archives using clean-room compression (v5 format)
- [ ] **US-21.4** As a developer, I can read RAR v4 headers (marker, archive, file, end)
- [ ] **US-21.5** As a developer, I can read/write RAR v5 headers (vint-based format)
- [ ] **US-21.6** As a user, I can handle RAR solid archives
- [ ] **US-21.7** As a user, I can handle RAR multi-volume archives
- [ ] **US-21.8** As a user, I can handle RAR recovery records
- [ ] **US-21.9** As a user, I can extract/create password-protected RAR archives (AES)

---

### Epic 22: FileFormat.Arj (Should Have)

- [ ] **US-22.1** As a user, I can list/extract files from ARJ archives
- [ ] **US-22.2** As a user, I can create ARJ archives
- [ ] **US-22.3** As a developer, I can read/write ARJ header structures

### Epic 23: FileFormat.Cab (Should Have)

- [ ] **US-23.1** As a user, I can list/extract files from CAB archives
- [ ] **US-23.2** As a user, I can create CAB archives
- [ ] **US-23.3** As a developer, I can handle MSZIP, LZX, and Quantum decompression
- [ ] **US-23.4** As a developer, I can handle multi-cabinet spanning

### Epic 24: FileFormat.Lzh (Should Have)

- [ ] **US-24.1** As a user, I can list/extract files from LZH/LHA archives
- [ ] **US-24.2** As a user, I can create LZH/LHA archives
- [ ] **US-24.3** As a developer, I can handle compression methods lh0 through lh7 and lzs

### Epic 25: FileFormat.Zoo (Should Have)

- [ ] **US-25.1** As a user, I can list/extract files from ZOO archives
- [ ] **US-25.2** As a developer, I can read ZOO archive and directory entry headers

### Epic 26: FileFormat.Arc (Should Have)

- [ ] **US-26.1** As a user, I can list/extract files from ARC archives
- [ ] **US-26.2** As a developer, I can handle all ARC compression methods (stored, packed, squeezed, crunched, squashed)

### Epic 27: FileFormat.Compress (Should Have)

- [ ] **US-27.1** As a user, I can decompress Unix `.Z` files
- [ ] **US-27.2** As a user, I can compress files into `.Z` format

### Epic 28: FileFormat.Lzop (Should Have)

- [ ] **US-28.1** As a user, I can decompress/compress `.lzo` files
- [ ] **US-28.2** As a developer, I can read/write LZOP headers and checksums

### Epic 29: FileFormat.Cpio (Should Have)

- [ ] **US-29.1** As a user, I can list/extract files from CPIO archives (binary, odc, newc, CRC formats)
- [ ] **US-29.2** As a user, I can create CPIO archives

---

### Epic 30: FileFormat.Ace (Could Have)

- [ ] **US-30.1** As a user, I can list/extract files from ACE archives (v1 and v2)
- [ ] **US-30.2** As a developer, I can read ACE headers and handle ACE compression

### Epic 31: FileFormat.Sqx (Could Have)

- [ ] **US-31.1** As a user, I can list/extract files from SQX archives
- [ ] **US-31.2** As a developer, I can handle SQX multi-algorithm entries

### Epic 32: FileFormat.Lz4 (Could Have)

- [ ] **US-32.1** As a user, I can decompress/compress `.lz4` files (frame format)
- [ ] **US-32.2** As a developer, I can handle LZ4 block and frame formats, including linked blocks

### Epic 33: FileFormat.Brotli (Could Have)

- [ ] **US-33.1** As a user, I can decompress/compress `.br` files
- [ ] **US-33.2** As a developer, I can handle Brotli's static dictionary, context modeling, and meta-blocks

### Epic 34: FileFormat.Snappy (Could Have)

- [ ] **US-34.1** As a user, I can decompress/compress Snappy-framed data
- [ ] **US-34.2** As a developer, I can handle both Snappy raw and framing formats

### Epic 35: Remaining Exotic Formats (Could Have)

- [ ] **US-35.1** FileFormat.Pak — list/extract PAK archives
- [ ] **US-35.2** FileFormat.Ha — list/extract HA archives (HSC/ASC arithmetic coding)
- [ ] **US-35.3** FileFormat.Zpaq — list/extract ZPAQ archives (context-mixing decompression)
- [ ] **US-35.4** FileFormat.Rzip — decompress/compress RZIP files
- [ ] **US-35.5** FileFormat.Stuffit — list/extract StuffIt archives (.sit, .sitx)
- [ ] **US-35.6** FileFormat.Dms — list/extract Amiga DMS disk archives
- [ ] **US-35.7** FileFormat.Alz — list/extract ALZ archives
- [ ] **US-35.8** FileFormat.Egg — list/extract EGG archives
- [ ] **US-35.9** FileFormat.Wim — list/extract WIM images (LZX/XPRESS decompression)
- [ ] **US-35.10** FileFormat.Kgb — list/extract KGB archives (PAQ-family decompression)
- [ ] **US-35.11** FileFormat.Pea — list/extract PEA archives
- [ ] **US-35.12** FileFormat.Uha — list/extract UHA archives
- [ ] **US-35.13** FileFormat.Shar — extract shell archives

---

### Epic 36: Compression.CLI

- [ ] **US-36.1** As a user, I can run `cwb list <archive>` to list contents of any supported archive
- [ ] **US-36.2** As a user, I can run `cwb extract <archive> [files...] -o <dir>` to extract files
- [ ] **US-36.3** As a user, I can run `cwb create <archive> <files...>` to create an archive (format auto-detected from extension)
- [ ] **US-36.4** As a user, I can run `cwb test <archive>` to verify archive integrity
- [ ] **US-36.5** As a user, I can run `cwb info <archive>` to see detailed format and compression info
- [ ] **US-36.6** As a user, I can run `cwb convert <input-archive> <output-archive>` to convert between formats
- [ ] **US-36.7** As a user, I can run `cwb benchmark <file>` to compare compression ratios and speeds across algorithms
- [ ] **US-36.8** As a user, I can set compression level, method, password, and other options via CLI flags
- [ ] **US-36.9** As a user, I see progress bars and throughput statistics during operations

### Epic 37: Compression.WinForms

- [ ] **US-37.1** As a user, I can open any supported archive and see its contents in a file list view
- [ ] **US-37.2** As a user, I can extract selected files or entire archives via the GUI
- [ ] **US-37.3** As a user, I can create new archives by dragging files into the window
- [ ] **US-37.4** As a user, I can choose the output format, compression method, and level when creating archives
- [ ] **US-37.5** As a user, I can set and enter passwords for encrypted archives
- [ ] **US-37.6** As a user, I can see file properties (size, compressed size, ratio, method, CRC) in columns
- [ ] **US-37.7** As a user, I can navigate nested archives (archives within archives)
- [ ] **US-37.8** As a user, I can preview text files within archives without extracting
- [ ] **US-37.9** As a user, I see progress dialogs with cancel support during long operations
- [ ] **US-37.10** As a user, I can associate file extensions with the application via settings
- [ ] **US-37.11** As a user, I can use context menu shell integration (right-click → Extract here / Compress)

### Epic 38: Compression.Analyzer.WinForms — Guided Analysis Wizard

- [ ] **US-38.1** As a user, I can open any arbitrary file and the analyzer attempts automatic format detection via magic bytes / signatures
- [ ] **US-38.2** As a user, I see a hex viewer showing the raw file content with highlighted regions for detected structures
- [ ] **US-38.3** As a user, I can step through a guided wizard that walks me through header identification
- [ ] **US-38.4** As a user, the wizard can detect compression primitives (Deflate, LZMA, Huffman, etc.) within arbitrary data by entropy analysis and trial decompression
- [ ] **US-38.5** As a user, I can provide known samples (original + compressed) and the analyzer detects which compression algorithm was used
- [ ] **US-38.6** As a user, I can invoke an external compression tool and the analyzer compares its output to detect the algorithm
- [ ] **US-38.7** As a user, I see entropy graphs (Shannon entropy per block/offset) to identify compressed vs uncompressed regions
- [ ] **US-38.8** As a user, I can annotate and label discovered structures, building up a format description
- [ ] **US-38.9** As a user, I can export analysis results as a structured report (JSON/HTML)
- [ ] **US-38.10** As a user, I can save and reload analysis sessions for iterative investigation
- [ ] **US-38.11** As a user, I can define custom header/structure templates and match them against files
- [ ] **US-38.12** As a user, the analyzer can detect embedded archives within larger files (e.g., EXE with appended ZIP)
- [ ] **US-38.13** As a user, I can perform byte-frequency analysis and n-gram analysis to profile unknown data
- [ ] **US-38.14** As a user, I can compare two files side-by-side to identify structural differences

---

### Epic 39: Testing

- [ ] **US-39.1** Every primitive in `Compression.Core` has round-trip tests (compress → decompress → verify identical)
- [ ] **US-39.2** Every `FileFormat.*` project has tests using real archive samples for extraction
- [ ] **US-39.3** Every `FileFormat.*` project has tests verifying created archives can be read by standard tools
- [ ] **US-39.4** Edge cases are tested: empty files, single-byte files, large files (>4 GB), highly repetitive data, random data
- [ ] **US-39.5** Performance benchmarks are tracked as tests (compression ratio and speed)
- [ ] **US-39.6** Cross-format tests verify that `cwb convert` produces correct output
- [ ] **US-39.7** Fuzzing harnesses exist for all decompression paths

---

## References & Inspiration

- **RFCs**: [RFC 1951](https://www.rfc-editor.org/rfc/rfc1951) (Deflate), [RFC 1952](https://www.rfc-editor.org/rfc/rfc1952) (Gzip), [RFC 1950](https://www.rfc-editor.org/rfc/rfc1950) (Zlib)
- **[PNGCrushCS](https://github.com/Hawkynt/PNGCrushCS)** — Zopfli-style optimal Deflate compression reference
- **[libxad](https://github.com/ashang/libxad)** — The eXternal Archive Decompressor, an Amiga-origin library supporting 50+ archive formats (ACE, ARC, ARJ, DMS, LZH/LZX, ZOO, and many more). A rich source of format documentation and ideas for exotic/legacy format support.
- **[XADMaster / The Unarchiver](https://github.com/MacPaw/XADMaster)** — Modern Objective-C continuation of libxad, used by The Unarchiver on macOS
- **[unar](https://github.com/ashang/unar)** - The Unarchiver unarchiving engine
- **[libarchive](https://github.com/libarchive/libarchive)** - Multi-Format extractor
- **[Wikipedia](https://en.wikipedia.org/wiki/List_of_archive_formats)** - A list of known formats
- **[ArchiveTeam](http://justsolve.archiveteam.org/wiki/Compression)** - Another list of known formats
- **[ArchiveTeam](http://justsolve.archiveteam.org/wiki/Archiving)** - A list of archivers
---

## Architecture Principles

1. **No external compression code** — Every algorithm is implemented from scratch in C#. We reference RFCs, specifications, and the sources listed above for algorithm descriptions and format ideas — but no compression source code is copied.
2. **Composable primitives** — `Compression.Core` provides building blocks; `FileFormat.*` projects compose them. For example, `FileFormat.Zip` uses `Compression.Core.Deflate` + `Compression.Core.Checksums.Crc32`.
3. **Stream-oriented** — All compression/decompression operates on `System.IO.Stream` for memory efficiency.
4. **Immutable headers** — File format header structures are immutable record types for thread safety and clarity.
5. **Testability** — Every component is independently testable. No hidden static state.
6. **Progressive complexity** — Start with decompression (simpler), then add compression. Start with simple formats (GZIP), then complex (7z, RAR).

## Implementation Order (Recommended)

```
Phase 1: Foundation
  └── Compression.Core (BitReader/Writer, CRC-32, Huffman, LZ77/LZSS)
  └── Compression.Tests (NUnit scaffold)

Phase 2: First Format
  └── FileFormat.Gzip (simplest real format — Deflate in a thin wrapper)
  └── FileFormat.Zip (builds on Deflate, most common format)

Phase 3: Core Expansion
  └── BWT, MTF, LZMA, Range Coding
  └── FileFormat.Bzip2, FileFormat.Xz

Phase 4: Complex Formats
  └── FileFormat.Tar, FileFormat.SevenZip, FileFormat.Rar
  └── FileFormat.Zstd (ANS/FSE engine)

Phase 5: Applications
  └── Compression.CLI
  └── Compression.WinForms

Phase 6: Legacy & Exotic
  └── FileFormat.Arj, .Cab, .Lzh, .Zoo, .Arc, .Compress, .Lzop, .Cpio
  └── FileFormat.Ace, .Sqx, .Lz4, .Brotli, .Snappy, and remaining exotic formats

Phase 7: Analyzer
  └── Compression.Analyzer.WinForms
```

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

## Compression.CLI — `cwb` Command-Line Tool

A universal archive tool supporting **40 formats** with smart conversion and optimal re-encoding.

### Installation

```bash
dotnet run --project Compression.CLI -- <command> [options]
# Or after publishing:
cwb <command> [options]
```

### Commands

| Command | Alias | Description |
|---------|-------|-------------|
| `list <archive>` | `l` | List contents of an archive |
| `extract <archive> [files...]` | `x` | Extract files from an archive |
| `create <archive> <files...>` | `c` | Create a new archive |
| `test <archive>` | `t` | Test archive integrity |
| `info <archive>` | — | Show detailed archive information |
| `convert <input> <output>` | — | Convert between archive formats |
| `optimize <input> <output>` | `opt` | Re-encode with optimal compression |
| `benchmark <file>` | `bench` | Compare compression across algorithms |
| `formats` | — | List all supported formats |

### Global Options

| Option | Alias | Description |
|--------|-------|-------------|
| `--password <pw>` | `-p` | Password for encrypted archives |
| `--method <method>` | `-m` | Compression method (see below) |

### Supported Formats (40)

**Archive formats (20):**
zip, rar, 7z, tar, cab, lzh, arj, arc, zoo, ace, sqx, cpio, ar, wim, rpm, deb, shar, pak, iso\*, udf\*

**Compression formats (14):**
gzip, bzip2, xz, zstd, lz4, brotli, snappy, lzop, compress (.Z), lzma, lzip, zlib, szdd, kwaj

**Compound formats (6):**
tar.gz, tar.bz2, tar.xz, tar.zst, tar.lz4, tar.lz

\* = detection only (format recognized but no read/write support yet)

### Compression Methods (`--method` / `-m`)

The `--method` option controls which compression algorithm is used when creating or converting archives. Append `+` to any method name to enable the **optimal encoder** — same decoder compatibility, better compression ratio, slower encoding.

**ZIP methods:**

| Method | Description |
|--------|-------------|
| `store` | No compression |
| `deflate` | Standard Deflate (default) |
| `deflate+` | Zopfli-optimized Deflate (significantly smaller) |
| `deflate64` | Enhanced Deflate (64KB window) |
| `shrink` | LZW with partial clearing (legacy) |
| `reduce` | Follower sets (legacy) |
| `implode` | Shannon-Fano trees (legacy) |
| `bzip2` | BWT + Huffman |
| `lzma` | LZMA |
| `ppmd` | PPMd prediction |
| `zstd` | Zstandard |

**7z methods:**

| Method | Description |
|--------|-------------|
| `lzma2` | LZMA2 (default) |
| `lzma2+` | LZMA2 with larger dictionary (64MB) |
| `lzma` | LZMA |
| `deflate` | Deflate |
| `bzip2` | BZip2 |
| `ppmd` | PPMd |

**Stream format optimization:**

| Format | Default | With `+` |
|--------|---------|----------|
| Gzip / Zlib | Deflate (default level) | Zopfli optimal Deflate |
| XZ / LZMA / Lzip | LZMA (normal) | LZMA (best) |
| Zstd | Zstandard (default) | Zstandard (best) |
| LZ4 | LZ4 (fast) | LZ4 HC (max) |
| Brotli | Brotli (default) | Brotli (best) |
| Compress (.Z) | LZW (default) | LZW (optimal) |
| LZOP | LZO1X (fast) | LZO1X-999 |

### Directory Support

The `create` command accepts both files and directories. When a directory is given, it is recursed and all contents are added with their relative paths preserved:

```bash
# Add a directory (preserves structure) and standalone files
cwb create output.zip myDir file1.txt file2.txt

# Wildcards are also supported
cwb create output.tar.gz *.txt myDir
```

Formats that support directory entries (ZIP, 7z, TAR, ARJ, CPIO) store them as explicit entries. Flat formats without path support (ARC, ZOO, PAK, Shar) flatten all files to the archive root.

### 3-Tier Conversion Model

When converting between formats (`cwb convert`), the tool automatically selects the most efficient strategy:

**Tier 1 — Bitstream transfer** (zero decompression):
Raw compressed bytes are moved between containers without any decompression. This is possible when the source and destination use the same compression codec.

```bash
# Deflate bitstream transferred directly — no decompression needed
cwb convert archive.gz archive.zlib
cwb convert archive.zip archive.gz      # single Deflate entry
cwb convert archive.gz archive.zip
```

**Tier 2 — Container restream** (decompress + recompress wrapper only):
The outer compression wrapper is changed, but the inner payload passes through untouched.

```bash
# Only the outer compression changes — tar data is not parsed
cwb convert data.tar.gz data.tar.xz

# Strip or add outer compression
cwb convert data.tar.gz data.tar
cwb convert data.tar data.tar.zst

# Restream content between different codecs
cwb convert data.gz data.xz
```

**Tier 3 — Full recompress** (extract + re-encode):
All content is extracted and re-encoded. This is the fallback for incompatible formats, and is also used when:
- Converting between different archive formats (zip → 7z)
- Changing the compression method within the same format
- Using `+` optimization (e.g. `--method deflate+`)

```bash
# Full recompress — different archive formats
cwb convert archive.zip archive.7z

# Full recompress with optimal encoding
cwb convert archive.zip optimized.zip --method deflate+

# Method change within same format
cwb convert archive.zip recompressed.zip --method lzma
```

### Examples

```bash
# List contents
cwb list archive.zip
cwb l archive.tar.gz

# Extract
cwb extract archive.7z -o ./output
cwb x archive.rar -p mypassword

# Create with default method
cwb create output.zip file1.txt file2.txt
cwb c output.tar.gz *.txt

# Create with specific method
cwb create output.zip file.txt --method store
cwb create output.zip file.txt --method deflate+
cwb create output.7z file.txt --method lzma2+

# Test integrity
cwb test archive.zip

# Show info
cwb info archive.rar

# Convert between formats
cwb convert input.tar.gz output.tar.xz      # tier 2: fast restream
cwb convert input.gz output.zlib             # tier 1: bitstream transfer
cwb convert input.zip output.7z              # tier 3: full recompress
cwb convert input.zip output.zip -m deflate+ # tier 3: optimize

# Optimize (re-encode with best compression)
cwb optimize input.zip optimized.zip
cwb opt input.gz optimized.gz

# Benchmark compression algorithms
cwb benchmark largefile.bin
```

---

## License

LGPL-3
