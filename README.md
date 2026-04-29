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

Formats in the canonical archive sense — ZIP, TAR, 7z, RAR, CAB, CPIO, and their relatives. They were designed as "a bag of files with a directory". These are covered in the [Archive Formats](#archive-formats), [ZIP-Derived Containers](#zip-derived-containers), [OLE2 Compound File Variants](#ole2-compound-file-variants), [Compound Formats](#compound-formats), and [Modern Packaging](#modern-packaging) tables.

### Pseudo-archives

Formats that *are* archives by structure but have never been presented that way in ordinary file managers. CompressionWorkbench slices each one along its natural payload boundary and exposes the same `List` / `Extract` surface as ZIP.

| Container                              | Entries become                                                                     | Where shipped                                          |
| -------------------------------------- | ---------------------------------------------------------------------------------- | ------------------------------------------------------ |
| **PE resource DLLs/EXEs**              | one entry per resource: `RT_GROUP_ICON` → `.ico`, `RT_BITMAP` → `.bmp`, `RT_MANIFEST` → `.xml`, `RT_STRING` → `.txt`, `RT_VERSION` → `.rcv`, raw `RT_RCDATA` | `FileFormat.PeResources`, `FileFormat.ResourceDll`     |
| **ICO / CUR / ANI**                    | one entry per `ICONDIRENTRY` → `.png` / `.bmp` (cursor adds hotspot)               | `FileFormat.Ico`, `FileFormat.PngCrushAdapters.Ani`    |
| **Multi-page TIFF / BigTIFF**          | one single-page `.tif` per IFD                                                     | `FileFormat.PngCrushAdapters.Tiff` / `BigTiff`         |
| **Multi-frame GIF / MNG / FLI / DCX**  | one `.gif` / `.png` per frame                                                      | `FileFormat.Gif`, `PngCrushAdapters.{Mng,Fli,Dcx}`     |
| **Animated PNG (APNG)**                | one `.png` per frame with dispose/blend applied against previous frames            | `FileFormat.PngCrushAdapters.Apng`                     |
| **Icon containers (ICNS, MPO)**        | Apple icon suite / stereoscopic JPEG pair                                          | `FileFormat.PngCrushAdapters.{Icns,Mpo}`               |
| **Font collections (TTC / OTC)**       | one `.ttf` / `.otf` per member font                                                | `FileFormat.FontCollection`                            |
| **Single-font (TTF / OTF)**            | per-glyph entries (cmap + glyf slicing; CFF/OpenType passes through)               | `FileFormat.FontCollection.Ttf`                        |
| **Gettext MO / PO**                    | one `.txt` per msgid/msgstr pair                                                   | `FileFormat.Gettext`                                   |
| **WAV / FLAC / MP3**                   | full file + per-channel WAV + ID3v2/RIFF metadata + APIC cover art                 | `FileFormat.Wav`, `FileFormat.Flac`, `FileFormat.Mp3`  |
| **Ogg**                                | per-logical-stream packets + Vorbis/Opus comments                                  | `FileFormat.Ogg`                                       |
| **MP4 / MOV / MKV / WebM**             | demuxed tracks (H.264 → Annex-B), attachments, chapters                            | `FileFormat.Mp4`, `FileFormat.Matroska`                |
| **MPEG Transport Stream**              | per-PID elementary streams (video/audio/data)                                      | `FileFormat.MpegTs`                                    |
| **Blu-ray PGS (SUP)**                  | subtitle segments grouped by epoch                                                 | `FileFormat.Sup`                                       |
| **VobSub (DVD)**                       | `.idx` metadata + per-entry slices of the sibling `.sub` PES stream                | `FileFormat.VobSub`                                    |
| **HLS M3U8**                           | segment list with per-variant metadata                                             | `FileFormat.M3u8`                                      |
| **U-Boot uImage, FDT/DTB, UEFI FV**    | firmware header metadata + decompressed payload or per-FFS/property entries        | `FileFormat.UImage`, `FileFormat.Dtb`, `FileFormat.UefiFv` |
| **Device executable packers**          | the packer's `metadata.ini` (detection evidence) + `packed_payload.bin` (or in-process decompressed body for UPX) | `FileFormat.ExePackers`                                |

### Honest failure

Formats that cannot produce multiple addressable entries stay in `FormatCategory.Stream` rather than falsely advertising themselves as archives. `IArchiveFormatOperations.List` is free to return a single "whole payload" entry for stream-style containers (and does, for formats like PAQ8 or the audio-stream-as-archive descriptors), but a format that would have to fake an index has no business claiming `SupportsMultipleEntries`.

---

## Solution Structure

The solution uses the `.slnx` XML format. All projects sit at the repository root — no `src/` or `tests/` directories. Solution folders in the IDE group projects logically; the filesystem stays flat so `git log --follow` works on every file.

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
+-- Compression.Tests                NUnit test project (tests)
|
+-- FileFormat.*                     One project per archive / stream / pseudo-archive / packer
+-- FileSystem.*                     One project per filesystem image format
+-- Codec.*                          Standalone audio codecs (PCM/FLAC/A-law/µ-law/GSM/ADPCM/MIDI/MP3/Vorbis/Opus/AAC)
```

Adding a new format is a three-step process:

1. Create a `FileFormat.<Name>/` or `FileSystem.<Name>/` project with a class implementing `IFormatDescriptor` (and `IStreamFormatOperations` or `IArchiveFormatOperations` as appropriate).
2. Add a `<ProjectReference>` from `Compression.Lib.csproj`.
3. Add the project to `CompressionWorkbench.slnx`.

The Roslyn source generator (`Compression.Registry.Generator`) discovers every implementation at compile time and emits the registration table. No reflection, no hand-maintained switch statements, no init hooks.

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

| Level           | Meaning                                                                                    |
| --------------- | ------------------------------------------------------------------------------------------ |
| **Unsupported** | No descriptor exists.                                                                      |
| **Read-only**   | Can list and extract; no creation.                                                         |
| **WORM**        | Write-Once-Read-Many — can produce a fresh archive/image, cannot modify one in place.      |
| **R/W**         | Can also add/replace/remove entries in an existing archive in place (no formats yet).      |

In tables below, `Yes` = WORM (or better), `-` = Read-only. A *Reference* column links to the authoritative spec or reverse-engineering document the implementation was validated against.

### Building blocks

The raw algorithm primitives registered via `IBuildingBlock`. They operate on `ReadOnlySpan<byte>` without any container framing — this is the surface the benchmark tool compares. Building blocks live in `Compression.Core`; they are never wrapped as `FileFormat.*` projects.

| Id              | Name                 | Family         | Description                                                                      | Reference                                                                                                                |
| --------------- | -------------------- | -------------- | -------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| BB_Deflate      | [DEFLATE](https://en.wikipedia.org/wiki/Deflate)                 | Dictionary     | LZ77 + Huffman, the algorithm inside gzip/zip/png                                | [RFC 1951](https://www.rfc-editor.org/rfc/rfc1951)                                                                       |
| BB_Deflate64    | [Deflate64](https://en.wikipedia.org/wiki/Deflate#Deflate64)     | Dictionary     | Enhanced DEFLATE with 64 KB window and extended codes                            | [MS-ZIP spec](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-zip/)                                    |
| BB_Lz77         | [LZ77](https://en.wikipedia.org/wiki/LZ77_and_LZ78)              | Dictionary     | Sliding-window dictionary with distance/length tokens                            | [Ziv & Lempel 1977 paper](https://www2.cs.duke.edu/courses/spring03/cps296.5/papers/ziv_lempel_1977_universal_algorithm.pdf) |
| BB_Lz78         | [LZ78](https://en.wikipedia.org/wiki/LZ77_and_LZ78)              | Dictionary     | Builds phrase dictionary from input, predecessor to LZW                          | [Ziv & Lempel 1978 paper](https://ieeexplore.ieee.org/document/1055934)                                                  |
| BB_Lzw          | [LZW](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Welch) | Dictionary | Lempel-Ziv-Welch dictionary coding, used in GIF and Unix `compress`              | [Welch 1984 paper](https://www.cs.duke.edu/courses/spring03/cps296.5/papers/welch_1984_technique_for.pdf)                |
| BB_Lzo          | [LZO1X](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Oberhumer) | Dictionary | Extremely fast dictionary compression optimised for decompression speed  | [oberhumer.com](http://www.oberhumer.com/opensource/lzo/)                                                                |
| BB_Lzss         | [LZSS](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Storer%E2%80%93Szymanski) | Dictionary | LZ77 variant with flag-bit encoding                                    | [Storer & Szymanski 1982](https://dl.acm.org/doi/10.1145/322344.322346)                                                  |
| BB_Lz4          | [LZ4](https://en.wikipedia.org/wiki/LZ4_(compression_algorithm)) | Dictionary     | Extremely fast LZ77-family block compression                                     | [LZ4 block format](https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md)                                          |
| BB_Snappy       | [Snappy](https://en.wikipedia.org/wiki/Snappy_(compression))     | Dictionary     | Fast LZ77-family compression (Google)                                            | [Snappy format](https://github.com/google/snappy/blob/main/format_description.txt)                                       |
| BB_Brotli       | [Brotli](https://en.wikipedia.org/wiki/Brotli)                   | Dictionary     | Modern LZ77 + Huffman with static dictionary (Google)                            | [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932)                                                                       |
| BB_Lzma         | [LZMA](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Markov_chain_algorithm) | Dictionary | Lempel-Ziv-Markov chain with range coding                       | [7-Zip LZMA SDK](https://www.7-zip.org/sdk.html)                                                                         |
| BB_Lzx          | [LZX](https://en.wikipedia.org/wiki/LZX)                         | Dictionary     | LZ77 + Huffman used in CAB/CHM/WIM                                               | [MS-PATCH LZX spec](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-patch/)                             |
| BB_Xpress       | [XPRESS Huffman](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-xca/) | Dictionary | Windows XPRESS (NTFS/WIM/Hyper-V) | [MS-XCA spec](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-xca/)                                     |
| BB_Lzh          | [LZH (LH5)](https://en.wikipedia.org/wiki/LHA_(file_format))     | Dictionary     | Lempel-Ziv with adaptive Huffman, used in LHA                                    | [LZH format doc](http://web.archive.org/web/20021013020141/http://www.osirusoft.com/joejared/lzhformat.html)             |
| BB_Arj          | [ARJ](https://en.wikipedia.org/wiki/ARJ)                         | Dictionary     | Modified LZ77 + Huffman used in ARJ archives                                     | [ARJ technical info](http://www.arjsoftware.com/)                                                                        |
| BB_Lzms         | [LZMS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-xca/) | Dictionary | LZ + Markov + Shannon with delta matching (Windows WIM/ESD)       | [MS-XCA LZMS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-xca/)                                    |
| BB_Lzp          | [LZP](https://en.wikipedia.org/wiki/LZP)                         | Dictionary     | Lempel-Ziv Prediction, context-based match prediction                            | [Bloom 1996](https://encode.su/threads/1301-LZP)                                                                         |
| BB_Ace          | [ACE](https://en.wikipedia.org/wiki/ACE_(compressed_file_format)) | Dictionary    | LZ77 + Huffman from ACE archive format                                           | [unace-nonfree](https://gitlab.com/luser_droog/unace-nonfree)                                                            |
| BB_Rar          | [RAR5](https://en.wikipedia.org/wiki/RAR_(file_format))          | Dictionary     | LZ + Huffman + PPM from RAR5                                                     | [rarlab technote](https://www.rarlab.com/technote.htm)                                                                   |
| BB_Sqx          | SQX                                                              | Dictionary     | LZ + Huffman from the SQX archive format                                         | [SqxFormat notes](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Compression.Core/SqxFormat/README.md)        |
| BB_ROLZ         | [ROLZ](https://en.wikipedia.org/wiki/LZ77_and_LZ78#Variants)     | Dictionary     | Reduced-Offset LZ with context-based match tables                                | [encode.su discussion](https://encode.su/threads/1909-ROLZ)                                                              |
| BB_PPM          | [PPM](https://en.wikipedia.org/wiki/Prediction_by_partial_matching) | Context Mixing | Prediction by Partial Matching, order-2 context modelling                     | [Cleary & Witten 1984](https://ieeexplore.ieee.org/document/1096090)                                                     |
| BB_CTW          | [CTW](https://en.wikipedia.org/wiki/Context_tree_weighting)      | Context Mixing | Context Tree Weighting — optimal universal compression                           | [Willems 1995 paper](https://ieeexplore.ieee.org/document/382012)                                                        |
| BB_LZHAM        | [LZHAM](https://github.com/richgel999/lzham_codec)               | Dictionary     | LZ77 + Huffman, inspired by Valve's LZHAM codec                                  | [LZHAM repo](https://github.com/richgel999/lzham_codec)                                                                  |
| BB_Lzs          | [LZS](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Stac) | Dictionary | Stac LZS (7/11-bit offset LZSS for networking)                                   | [RFC 1967](https://www.rfc-editor.org/rfc/rfc1967) / [RFC 2395](https://www.rfc-editor.org/rfc/rfc2395)                  |
| BB_Lzwl         | LZWL                                                             | Dictionary     | LZW with variable-length initial alphabet from digram analysis                   | [LZWL paper](https://www.jucs.org/jucs_9_9/compression_with_finite_mixed/jucs_9_9_1055_1081_salomon.pdf)                 |
| BB_RePair       | [Re-Pair](https://en.wikipedia.org/wiki/Re-Pair)                 | Dictionary     | Recursive Pairing, offline grammar-based compression                             | [Larsson & Moffat 1999](https://ieeexplore.ieee.org/document/755679)                                                     |
| BB_842          | [842](https://en.wikipedia.org/wiki/842_(compression_algorithm)) | Dictionary     | IBM 842 hardware compression with 2/4/8-byte template matching                   | [Linux `crypto/842*`](https://github.com/torvalds/linux/tree/master/crypto)                                              |
| BB_Huffman      | [Huffman](https://en.wikipedia.org/wiki/Huffman_coding)          | Entropy        | Optimal prefix-free entropy coding using symbol frequencies                      | [Huffman 1952](https://ieeexplore.ieee.org/document/4051119)                                                             |
| BB_Arithmetic   | [Arithmetic](https://en.wikipedia.org/wiki/Arithmetic_coding)    | Entropy        | Order-0 arithmetic coding with frequency table                                   | [Witten, Neal & Cleary 1987](https://dl.acm.org/doi/10.1145/214762.214771)                                               |
| BB_ShannonFano  | [Shannon-Fano](https://en.wikipedia.org/wiki/Shannon%E2%80%93Fano_coding) | Entropy | Historical predecessor to Huffman, recursive frequency splitting        | Shannon 1948                                                                                                             |
| BB_Golomb       | [Golomb/Rice](https://en.wikipedia.org/wiki/Golomb_coding)       | Entropy        | Optimal coding for geometric distributions                                       | [Golomb 1966](https://ieeexplore.ieee.org/document/1053899)                                                              |
| BB_Fibonacci    | [Fibonacci coding](https://en.wikipedia.org/wiki/Fibonacci_coding) | Entropy      | Universal code using Zeckendorf representation with `11` terminators             | [Apostolico & Fraenkel 1987](https://ieeexplore.ieee.org/document/1057294)                                              |
| BB_FSE          | [FSE/tANS](https://en.wikipedia.org/wiki/Asymmetric_numeral_systems) | Entropy    | Table-based Asymmetric Numeral Systems, used in Zstd                             | [Duda 2013 paper](https://arxiv.org/abs/1311.2540) / [Collet's blog](https://fastcompression.blogspot.com/)              |
| BB_BPE          | [Byte Pair Encoding](https://en.wikipedia.org/wiki/Byte_pair_encoding) | Entropy  | Iterative most-frequent pair replacement                                         | [Gage 1994](http://www.pennelynn.com/Documents/CUJ/HTML/94HTML/19940045.HTM)                                             |
| BB_RangeCoding  | [Range coding](https://en.wikipedia.org/wiki/Range_coding)       | Entropy        | Byte-oriented arithmetic coding variant with carryless normalisation             | [Martin 1979](https://en.wikipedia.org/wiki/Range_coding#References)                                                     |
| BB_rANS         | [rANS](https://en.wikipedia.org/wiki/Asymmetric_numeral_systems#Range_variants_(rANS)_and_streaming) | Entropy | Range ANS coder, used in AV1 and LZFSE              | [Duda 2013 paper](https://arxiv.org/abs/1311.2540)                                                                       |
| BB_ExpGolomb    | [Exp-Golomb](https://en.wikipedia.org/wiki/Exponential-Golomb_coding) | Entropy   | Exponential Golomb, used in H.264/H.265                                          | [Teuhola 1978](https://dl.acm.org/doi/10.1016/0020-0190(78)90043-2)                                                      |
| BB_Unary        | [Unary](https://en.wikipedia.org/wiki/Unary_coding)              | Entropy        | Simplest universal code: N ones followed by a zero                               | —                                                                                                                        |
| BB_EliasGamma   | [Elias gamma](https://en.wikipedia.org/wiki/Elias_gamma_coding)  | Entropy        | Universal code using unary length prefix                                         | [Elias 1975](https://ieeexplore.ieee.org/document/1055349)                                                               |
| BB_EliasDelta   | [Elias delta](https://en.wikipedia.org/wiki/Elias_delta_coding)  | Entropy        | Gamma-codes the bit length                                                       | [Elias 1975](https://ieeexplore.ieee.org/document/1055349)                                                               |
| BB_Levenshtein  | [Levenshtein coding](https://en.wikipedia.org/wiki/Levenshtein_coding) | Entropy  | Self-delimiting universal code with recursive length prefixing                   | Levenshtein 1968                                                                                                         |
| BB_Tunstall     | [Tunstall coding](https://en.wikipedia.org/wiki/Tunstall_coding) | Entropy        | Variable-to-fixed code, dual of Huffman                                          | Tunstall 1967 (PhD thesis)                                                                                               |
| BB_Dmc          | [DMC](https://en.wikipedia.org/wiki/Dynamic_Markov_compression)  | Entropy        | Dynamic Markov Compression, bit-level FSM with state cloning                     | [Cormack & Horspool 1987](https://dl.acm.org/doi/10.1145/22899.22901)                                                    |
| BB_Bwt          | [BWT](https://en.wikipedia.org/wiki/Burrows%E2%80%93Wheeler_transform) | Transform | Burrows-Wheeler Transform, reorders bytes for better compression                | [Burrows & Wheeler 1994](https://www.hpl.hp.com/techreports/Compaq-DEC/SRC-RR-124.pdf)                                   |
| BB_Mtf          | [MTF](https://en.wikipedia.org/wiki/Move-to-front_transform)     | Transform      | Move-to-Front Transform                                                          | [Bentley et al. 1986](https://dl.acm.org/doi/10.1145/6138.6151)                                                          |
| BB_Delta        | [Delta](https://en.wikipedia.org/wiki/Delta_encoding)            | Transform      | Delta filter, stores differences between consecutive bytes                       | —                                                                                                                        |
| BB_Rle          | [RLE](https://en.wikipedia.org/wiki/Run-length_encoding)         | Transform      | Run-Length Encoding                                                              | —                                                                                                                        |
| BB_Dpcm         | [DPCM](https://en.wikipedia.org/wiki/Differential_pulse-code_modulation) | Transform | Differential PCM, stores sample-to-sample differences                        | —                                                                                                                        |

The benchmark command (`cwb benchmark <file>` or the UI's Benchmark Tool) runs every building block over the supplied data, records ratio + compress/decompress times, and ranks the results.

### Archive formats

| Format     | Extensions     | Read | Write       | Reference                                                                                                              | Notes                                                                                                                                                       |
| ---------- | -------------- | ---- | ----------- | ---------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [ZIP](https://en.wikipedia.org/wiki/ZIP_(file_format))       | `.zip`        | Yes | Yes         | [APPNOTE.TXT](https://pkwaredownloads.blob.core.windows.net/pem/APPNOTE.txt)                                           | Store, Deflate, Deflate64, Shrink, Reduce, Implode, BZip2, LZMA, PPMd, Zstd, AES                                                                            |
| [RAR](https://en.wikipedia.org/wiki/RAR_(file_format))       | `.rar`        | Yes | Yes (v4/v5) | [rarlab technote](https://www.rarlab.com/technote.htm)                                                                 | v1-v5 decoders, solid, multi-volume, encryption, recovery                                                                                                   |
| [7z](https://en.wikipedia.org/wiki/7z)                       | `.7z`         | Yes | Yes         | [7-Zip format](https://www.7-zip.org/7z.html)                                                                          | LZMA/LZMA2, Deflate, BZip2, PPMd, BCJ/BCJ2, AES-256, multi-volume                                                                                           |
| [TAR](https://en.wikipedia.org/wiki/Tar_(computing))         | `.tar`        | Yes | Yes         | [POSIX ustar](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/pax.html)                                     | POSIX/GNU/PAX, multi-volume                                                                                                                                 |
| [CAB](https://en.wikipedia.org/wiki/Cabinet_(file_format))   | `.cab`        | Yes | Yes         | [MS-CAB](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cab/)                                        | MSZIP, LZX, Quantum                                                                                                                                         |
| [LZH/LHA](https://en.wikipedia.org/wiki/LHA_(file_format))   | `.lzh`,`.lha` | Yes | Yes         | [LHA archive format](http://www.math.sci.hiroshima-u.ac.jp/m-mat/MT/hamamura-home/lha-en.html)                         | lh0-lh7, lzs, lh1-lh3 (adaptive Huffman), pm0-pm2                                                                                                           |
| [ARJ](https://en.wikipedia.org/wiki/ARJ)                     | `.arj`        | Yes | Yes         | [ARJ technical](http://www.arjsoftware.com/)                                                                           | Methods 0-4, garble encryption                                                                                                                              |
| [ARC](https://en.wikipedia.org/wiki/ARC_(file_format))       | `.arc`        | Yes | Yes         | [ARC format](http://fileformats.archiveteam.org/wiki/ARC_(compression_format))                                         | Methods 0-9 (RLE, LZW, Squeeze, Huffman)                                                                                                                    |
| [ZOO](https://en.wikipedia.org/wiki/Zoo_(file_format))       | `.zoo`        | Yes | Yes         | [zoo format](http://fileformats.archiveteam.org/wiki/ZOO)                                                              | LZW, LZH                                                                                                                                                    |
| [ACE](https://en.wikipedia.org/wiki/ACE_(compressed_file_format)) | `.ace`   | Yes | Yes         | [ACE unofficial spec](https://github.com/droe/acefile/blob/master/acefile.py)                                          | ACE 1.0/2.0, solid, sound/picture filters, Blowfish, recovery                                                                                               |
| SQX                                                          | `.sqx`        | Yes | Yes         | [SQX disassembly](https://encode.su/threads/1290-SQX-(by-SpeedProject))                                                | LZH, multimedia, audio, solid, AES-128, recovery                                                                                                            |
| [CPIO](https://en.wikipedia.org/wiki/Cpio)                   | `.cpio`       | Yes | Yes         | [cpio(5)](https://www.freebsd.org/cgi/man.cgi?query=cpio&sektion=5)                                                    | Binary, odc, newc, CRC                                                                                                                                      |
| [AR](https://en.wikipedia.org/wiki/Ar_(Unix))                | `.ar`         | Yes | Yes         | [ar(5)](https://www.freebsd.org/cgi/man.cgi?query=ar&sektion=5)                                                        | Unix archive                                                                                                                                                |
| [WIM](https://en.wikipedia.org/wiki/Windows_Imaging_Format)  | `.wim`        | Yes | Yes         | [Imagex WIM format](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/)                           | LZX, XPRESS                                                                                                                                                 |
| [RPM](https://en.wikipedia.org/wiki/RPM_Package_Manager)     | `.rpm`        | Yes | Yes         | [RPM spec](https://rpm-software-management.github.io/rpm/manual/format.html)                                           | CPIO payload                                                                                                                                                |
| [DEB](https://en.wikipedia.org/wiki/Deb_(file_format))       | `.deb`        | Yes | Yes         | [deb(5)](https://man7.org/linux/man-pages/man5/deb.5.html)                                                             | AR+TAR with gz/xz/zst/bz2                                                                                                                                   |
| [Shar](https://en.wikipedia.org/wiki/Shar)                   | `.shar`       | Yes | Yes         | [GNU sharutils](https://www.gnu.org/software/sharutils/)                                                               | Shell archive                                                                                                                                               |
| PAK                                                          | `.pak`        | Yes | Yes         | [PAK spec](http://fileformats.archiveteam.org/wiki/PAK)                                                                | ARC-compatible                                                                                                                                              |
| [HA](https://en.wikipedia.org/wiki/HA_(file_format))         | `.ha`         | Yes | Yes         | [HA specification](http://fileformats.archiveteam.org/wiki/HA)                                                         | HSC/ASC arithmetic coding                                                                                                                                   |
| [ZPAQ](https://en.wikipedia.org/wiki/ZPAQ)                   | `.zpaq`       | Yes | Yes         | [ZPAQ spec PDF](https://mattmahoney.net/dc/zpaq206.pdf)                                                                | Context mixing, journaling                                                                                                                                  |
| [StuffIt](https://en.wikipedia.org/wiki/StuffIt)             | `.sit`        | Yes | Yes         | [libxad sit.c](https://github.com/MacPaw/XADMaster)                                                                    | Multiple methods                                                                                                                                            |
| StuffIt X                                                    | `.sitx`       | Yes | Yes         | [XADMaster StuffItX](https://github.com/MacPaw/XADMaster)                                                              | Detection-only; WORM emits a valid `StuffIt!` envelope (proprietary element-stream writer not implemented)                                                  |
| [SquashFS](https://en.wikipedia.org/wiki/SquashFS)           | `.sqfs`       | Yes | Yes         | [SquashFS 4.0 spec](https://dr-emann.github.io/squashfs/)                                                              | Filesystem image                                                                                                                                            |
| [CramFS](https://en.wikipedia.org/wiki/Cramfs)               | `.cramfs`     | Yes | Yes         | [Linux `fs/cramfs/`](https://github.com/torvalds/linux/tree/master/fs/cramfs)                                          | Filesystem image                                                                                                                                            |
| [NSIS](https://en.wikipedia.org/wiki/Nullsoft_Scriptable_Install_System) | `.exe` | Yes | Yes     | [NSIS wiki](https://nsis.sourceforge.io/Docs/)                                                                         | Installer extraction + WORM emits overlay-only data (no PE stub)                                                                                            |
| Inno Setup                                                   | `.exe`        | Yes | Yes         | [innounp](https://sourceforge.net/projects/innounp/)                                                                   | Installer extraction + WORM emits signature header (no PE stub)                                                                                             |
| [DMS](https://en.wikipedia.org/wiki/Disk_Masher_System)      | `.dms`        | Yes | Yes         | [xDMS source](https://github.com/markrabjohn/xDMS)                                                                     | Amiga disk archiver                                                                                                                                         |
| [LZX (Amiga)](https://en.wikipedia.org/wiki/LZX)             | `.lzx`        | Yes | Yes         | [Amiga LZX format](http://fileformats.archiveteam.org/wiki/LZX)                                                        | Amiga LZX                                                                                                                                                   |
| [Compact Pro](https://en.wikipedia.org/wiki/Compact_Pro)     | `.cpt`        | Yes | Yes         | [XADMaster cpt.c](https://github.com/MacPaw/XADMaster)                                                                 | Classic Mac format                                                                                                                                          |
| Spark                                                        | `.spark`      | Yes | Yes         | [RISC OS Spark](http://fileformats.archiveteam.org/wiki/Spark)                                                         | RISC OS format                                                                                                                                              |
| [LBR](https://en.wikipedia.org/wiki/LU_(software))           | `.lbr`        | Yes | Yes         | [CP/M LBR](http://www.gaby.de/cpm/manuals/archive/lbr.txt)                                                             | CP/M format                                                                                                                                                 |
| UHARC                                                        | `.uha`        | Yes | Yes         | [UHARC docs](http://www.uharc.com/)                                                                                    | LZP compression                                                                                                                                             |
| [WAD (Doom)](https://en.wikipedia.org/wiki/Doom_WAD)         | `.wad`        | Yes | Yes         | [Doom Wiki WAD](https://doomwiki.org/wiki/WAD)                                                                         | Doom WAD format                                                                                                                                             |
| WAD2/WAD3                                                    | `.wad`        | Yes | Yes         | [Quake Wiki WAD](https://quakewiki.org/wiki/.wad)                                                                      | Quake/Half-Life texture archive                                                                                                                             |
| [XAR](https://en.wikipedia.org/wiki/Xar_(archiver))          | `.xar`        | Yes | Yes         | [XAR on-disk format](https://github.com/mackyle/xar/wiki/xarformat)                                                    | Apple `.pkg` (zlib TOC)                                                                                                                                     |
| [ALZip](https://en.wikipedia.org/wiki/ALZip)                 | `.alz`        | Yes | Yes         | [ALZ format](http://fileformats.archiveteam.org/wiki/ALZ)                                                              | Korean archive (Deflate)                                                                                                                                    |
| VPK                                                          | `.vpk`        | Yes | Yes         | [Valve VPK](https://developer.valvesoftware.com/wiki/VPK_(file_format))                                                | Valve game archive                                                                                                                                          |
| BSA/BA2                                                      | `.bsa`,`.ba2` | Yes | Yes         | [BSA format](https://en.uesp.net/wiki/Skyrim_Mod:File_Formats/BSA)                                                     | Bethesda game archive                                                                                                                                       |
| [MPQ](https://en.wikipedia.org/wiki/MPQ)                     | `.mpq`        | Yes | Yes         | [ZezulaMPQ docs](https://github.com/ladislav-zezula/StormLib)                                                          | Blizzard — WORM v1 with stored entries, encrypted hash+block tables, self-referential `(listfile)`                                                          |
| [GRP](https://moddingwiki.shikadi.net/wiki/GRP_(Build)_Format) | `.grp`      | Yes | Yes         | [BUILD Engine docs](https://moddingwiki.shikadi.net/wiki/GRP_(Build)_Format)                                           | BUILD Engine (Duke Nukem 3D)                                                                                                                                |
| [HOG](https://en.wikipedia.org/wiki/HOG_(file_format))       | `.hog`        | Yes | Yes         | [Descent HOG](http://descent.wikia.com/wiki/HOG)                                                                       | Descent game archive                                                                                                                                        |
| BIG                                                          | `.big`        | Yes | Yes         | [EA BIG format](http://wiki.xentax.com/index.php/EA_BIG)                                                               | EA Games (C&C, FIFA)                                                                                                                                        |
| Godot PCK                                                    | `.pck`        | Yes | Yes         | [Godot PCK spec](https://docs.godotengine.org/en/stable/development/file_formats/pck.html)                             | Godot Engine resource pack                                                                                                                                  |
| [WARC](https://en.wikipedia.org/wiki/Web_ARChive)            | `.warc`       | Yes | Yes         | [ISO 28500](https://iipc.github.io/warc-specifications/)                                                               | Web archive — WORM emits one `resource` record per input file                                                                                               |
| NDS                                                          | `.nds`        | Yes | Yes         | [GBATEK NDS](https://problemkaputt.de/gbatek.htm)                                                                      | Nintendo DS ROM — WORM emits valid NitroFS (no ARM9/ARM7 boot code)                                                                                         |
| NSA                                                          | `.nsa`        | Yes | Yes         | [NScripter docs](https://www.nscripter.com/)                                                                           | NScripter — WORM writes stored entries (compression type 0)                                                                                                 |
| SAR                                                          | `.sar`        | Yes | Yes         | [NScripter docs](https://www.nscripter.com/)                                                                           | NScripter — uncompressed variant of NSA                                                                                                                     |
| PackIt                                                       | `.pit`        | Yes | -           | [XADMaster packit.c](https://github.com/MacPaw/XADMaster)                                                              | Classic Mac format                                                                                                                                          |
| DiskDoubler                                                  | `.dd`         | Yes | Yes         | [XADMaster DD](https://github.com/MacPaw/XADMaster)                                                                    | Classic Mac compression — WORM stores data fork (method 0)                                                                                                  |
| MSI                                                          | `.msi`        | Yes | Yes         | [MS-CFB](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/)                                        | OLE Compound File — WORM produces a CFB envelope (not a functional Installer DB)                                                                            |
| [PDF](https://en.wikipedia.org/wiki/PDF)                     | `.pdf`        | Yes | Yes         | [ISO 32000](https://www.iso.org/standard/75839.html)                                                                   | Image extraction + WORM via file attachments (EmbeddedFiles) — any file type round-trips                                                                    |
| [TNEF](https://en.wikipedia.org/wiki/Transport_Neutral_Encapsulation_Format) | `.tnef`,`.dat` | Yes | Yes | [MS-OXTNEF](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxtnef/)                      | Outlook `winmail.dat`                                                                                                                                       |
| Split File                                                   | `.001`        | Yes | Yes         | —                                                                                                                      | Multi-part file joining/splitting                                                                                                                           |
| FreeArc                                                      | `.arc`        | Yes | Yes         | [FreeArc source](https://github.com/Bulat-Ziganshin/FreeArc)                                                           | FreeArc archive                                                                                                                                             |
| [CHM](https://en.wikipedia.org/wiki/Microsoft_Compiled_HTML_Help) | `.chm`   | Yes | Yes         | [CHM file format](https://archive.org/details/chmspec)                                                                 | MS Compiled HTML Help — WORM stores files in section 0 (uncompressed); LZX compression available via options                                                |
| Wrapster                                                     | -             | Yes | -           | [XADMaster wrapster.c](https://github.com/MacPaw/XADMaster)                                                            | MP3 wrapper archive                                                                                                                                         |
| LhF                                                          | `.lhf`        | Yes | Yes         | [XADMaster](https://github.com/MacPaw/XADMaster)                                                                       | Amiga LhFloppy disk (LZH-compressed tracks)                                                                                                                 |
| ZAP                                                          | `.zap`        | Yes | Yes         | [XADMaster](https://github.com/MacPaw/XADMaster)                                                                       | Amiga disk archiver — WORM writes stored tracks                                                                                                             |
| PackDisk                                                     | `.pdsk`       | Yes | Yes         | [XADMaster](https://github.com/MacPaw/XADMaster)                                                                       | Amiga PackDisk — WORM writes stored tracks. Same writer covers DCS / xDisk / xMash via different magics.                                                    |
| AMPK                                                         | -             | Yes | -           | [XADMaster](https://github.com/MacPaw/XADMaster)                                                                       | Amiga AMPK                                                                                                                                                  |
| IFF-CDAF                                                     | -             | Yes | -           | [IFF spec](http://fileformats.archiveteam.org/wiki/IFF)                                                                | IFF-CDAF archive                                                                                                                                            |
| UMX                                                          | `.umx`        | Yes | Yes         | [Beyond Unreal wiki](https://wiki.beyondunreal.com/Legacy:Package_File_Format)                                         | Unreal package — WORM emits valid header (detection-only)                                                                                                   |

### ZIP-derived containers

All delegate to the ZIP reader/writer. WORM (`Yes`) means a fresh container can be produced with the correct internal layout.

| Format  | Extensions      | Read | Write | Reference                                                                                                          | Notes                                      |
| ------- | --------------- | ---- | ----- | ------------------------------------------------------------------------------------------------------------------ | ------------------------------------------ |
| [JAR](https://en.wikipedia.org/wiki/JAR_(file_format))  | `.jar`         | Yes | Yes | [JAR spec](https://docs.oracle.com/en/java/javase/21/docs/specs/jar/jar.html)                                        | Java archive                               |
| WAR     | `.war`          | Yes | Yes   | [Java EE WAR](https://docs.oracle.com/javaee/7/tutorial/packaging003.htm)                                          | Java web archive                           |
| EAR     | `.ear`          | Yes | Yes   | [Java EE EAR](https://docs.oracle.com/javaee/7/tutorial/packaging004.htm)                                          | Java enterprise archive                    |
| [APK](https://en.wikipedia.org/wiki/Apk_(file_format))  | `.apk`         | Yes | Yes | [Android APK](https://source.android.com/docs/core/runtime/jit-compiler)                                             | Android package                            |
| [IPA](https://en.wikipedia.org/wiki/.ipa)               | `.ipa`         | Yes | Yes | [Apple IPA bundle](https://developer.apple.com/documentation/)                                                       | iOS package                                |
| APPX    | `.appx`,`.msix` | Yes | Yes   | [MS-APPXPKG](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/)                                             | Windows package                            |
| [XPI](https://en.wikipedia.org/wiki/XPInstall)          | `.xpi`         | Yes | Yes | [Mozilla XPI](https://developer.mozilla.org/en-US/docs/Mozilla/Tech/XPI)                                             | Firefox extension                          |
| CRX     | `.crx`          | Yes | Yes   | [Chrome CRX3](https://developer.chrome.com/docs/extensions/mv3/linux_hosting/)                                     | Chrome extension — WORM emits unsigned CRX3 envelope (browser rejects signature) |
| [EPUB](https://en.wikipedia.org/wiki/EPUB)              | `.epub`        | Yes | Yes | [EPUB 3 spec](https://www.w3.org/TR/epub-33/)                                                                        | eBook                                      |
| MAFF    | `.maff`         | Yes | Yes   | [MAFF spec](http://maf.mozdev.org/maff-specification.html)                                                           | Mozilla Archive Format                     |
| [KMZ](https://en.wikipedia.org/wiki/Keyhole_Markup_Language) | `.kmz`    | Yes | Yes | [KML spec](https://www.ogc.org/standards/kml)                                                                         | Google Earth                               |
| NuPkg   | `.nupkg`        | Yes | Yes   | [NuGet spec](https://learn.microsoft.com/en-us/nuget/reference/nuspec)                                               | NuGet package                              |
| [DOCX](https://en.wikipedia.org/wiki/Office_Open_XML)   | `.docx`        | Yes | Yes | [ECMA-376](https://www.ecma-international.org/publications-and-standards/standards/ecma-376/)                         | OOXML Word                                 |
| XLSX    | `.xlsx`         | Yes | Yes   | [ECMA-376](https://www.ecma-international.org/publications-and-standards/standards/ecma-376/)                         | OOXML Excel                                |
| PPTX    | `.pptx`         | Yes | Yes   | [ECMA-376](https://www.ecma-international.org/publications-and-standards/standards/ecma-376/)                         | OOXML PowerPoint                           |
| [ODT](https://en.wikipedia.org/wiki/OpenDocument)       | `.odt`         | Yes | Yes | [OASIS ODF](https://www.oasis-open.org/standard/odf/)                                                                 | OpenDocument Text                          |
| ODS     | `.ods`          | Yes | Yes   | [OASIS ODF](https://www.oasis-open.org/standard/odf/)                                                                 | OpenDocument Spreadsheet                   |
| ODP     | `.odp`          | Yes | Yes   | [OASIS ODF](https://www.oasis-open.org/standard/odf/)                                                                 | OpenDocument Presentation                  |
| CBZ     | `.cbz`          | Yes | Yes   | [Comic book archive](https://en.wikipedia.org/wiki/Comic_book_archive)                                                | Comic book ZIP                             |
| CBR     | `.cbr`          | Yes | Yes   | [Comic book archive](https://en.wikipedia.org/wiki/Comic_book_archive)                                                | Comic book RAR — delegates to RarWriter    |

### OLE2 Compound File variants

Microsoft binary-office formats built on the [OLE2 / Compound File Binary (CFB)](https://en.wikipedia.org/wiki/Compound_File_Binary_Format) container. WORM creation produces a structurally-valid CFB envelope (that round-trips through our reader and other permissive CFB tools like libgsf / Apache POI) but is **not** a real Word/Excel/PowerPoint/Outlook document — those require generating each application's internal binary stream layout, which is out of scope. Limitations: ~6.8 MB total file size (109 FAT sectors, no DIFAT chain), single root storage, stream names ≤ 31 UTF-16 chars.

| Format    | Extensions  | Read | Write | Reference                                                                                  | Notes                                                           |
| --------- | ----------- | ---- | ----- | ------------------------------------------------------------------------------------------ | --------------------------------------------------------------- |
| DOC       | `.doc`      | Yes  | Yes   | [MS-DOC](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-doc/)          | Word 97-2003 (CFB envelope, not a real Word document)           |
| XLS       | `.xls`      | Yes  | Yes   | [MS-XLS](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-xls/)          | Excel 97-2003 (CFB envelope, not a real workbook)               |
| PPT       | `.ppt`      | Yes  | Yes   | [MS-PPT](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-ppt/)          | PowerPoint 97-2003 (CFB envelope, not a real presentation)      |
| MSG       | `.msg`      | Yes  | Yes   | [MS-OXMSG](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxmsg/) | Outlook message (CFB envelope, not real MAPI properties)       |
| Thumbs.db | `Thumbs.db` | Yes  | Yes   | [Forensics docs](https://www.forensicswiki.xyz/wiki/Thumbs.db)                             | Windows thumbnail cache (CFB envelope, not real Catalog layout) |
| MSI       | `.msi`      | Yes  | Yes   | [MS-MSI](https://learn.microsoft.com/en-us/windows/win32/msi/windows-installer-file-format) | Windows Installer (CFB envelope, not a functional Installer DB) |

### Compression stream formats

Single-stream compressors. Compress/Decompress indicate the two halves of the algorithm.

| Format      | Extensions        | Compress | Decompress | Reference                                                                                              |
| ----------- | ----------------- | -------- | ---------- | ------------------------------------------------------------------------------------------------------ |
| [Gzip](https://en.wikipedia.org/wiki/Gzip)                | `.gz`              | Yes  | Yes | [RFC 1952](https://www.rfc-editor.org/rfc/rfc1952)                                                     |
| [BZip2](https://en.wikipedia.org/wiki/Bzip2)              | `.bz2`             | Yes  | Yes | [bzip2 source](https://sourceware.org/bzip2/)                                                          |
| [XZ](https://en.wikipedia.org/wiki/XZ_Utils)              | `.xz`              | Yes  | Yes | [XZ format](https://tukaani.org/xz/xz-file-format.txt)                                                 |
| [Zstandard](https://en.wikipedia.org/wiki/Zstd)           | `.zst`             | Yes  | Yes | [RFC 8878](https://www.rfc-editor.org/rfc/rfc8878)                                                     |
| [LZ4](https://en.wikipedia.org/wiki/LZ4_(compression_algorithm)) | `.lz4`     | Yes  | Yes | [LZ4 frame format](https://github.com/lz4/lz4/blob/dev/doc/lz4_Frame_format.md)                         |
| [Brotli](https://en.wikipedia.org/wiki/Brotli)            | `.br`              | Yes  | Yes | [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932)                                                     |
| [Snappy](https://en.wikipedia.org/wiki/Snappy_(compression)) | `.sz`,`.snappy` | Yes  | Yes | [Snappy framing](https://github.com/google/snappy/blob/main/framing_format.txt)                         |
| [LZOP](https://en.wikipedia.org/wiki/Lzop)                | `.lzo`             | Yes  | Yes | [lzop source](https://www.lzop.org/)                                                                   |
| [compress (.Z)](https://en.wikipedia.org/wiki/Compress_(software)) | `.Z`       | Yes  | Yes | [ncompress](https://github.com/vapier/ncompress)                                                       |
| [LZMA](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Markov_chain_algorithm) | `.lzma` | Yes | Yes | [7-Zip LZMA SDK](https://www.7-zip.org/sdk.html)                                                  |
| [Lzip](https://en.wikipedia.org/wiki/Lzip)                | `.lz`              | Yes  | Yes | [lzip format](https://www.nongnu.org/lzip/manual/lzip_manual.html)                                     |
| [Zlib](https://en.wikipedia.org/wiki/Zlib)                | `.zlib`            | Yes  | Yes | [RFC 1950](https://www.rfc-editor.org/rfc/rfc1950)                                                     |
| SZDD                                                      | `.sz_`             | Yes  | Yes | [compress.exe format](https://www.stdlib.at/)                                                          |
| KWAJ                                                      | -                  | Yes  | Yes | [MS compress formats](http://fileformats.archiveteam.org/wiki/KWAJ)                                     |
| [RZIP](https://en.wikipedia.org/wiki/Rzip)                | `.rz`              | Yes  | Yes | [rzip docs](http://rzip.samba.org/)                                                                    |
| [MacBinary](https://en.wikipedia.org/wiki/MacBinary)      | `.bin`             | Yes  | Yes | [RFC 1740](https://www.rfc-editor.org/rfc/rfc1740)                                                     |
| [BinHex](https://en.wikipedia.org/wiki/BinHex)            | `.hqx`             | Yes  | Yes | [RFC 1741](https://www.rfc-editor.org/rfc/rfc1741)                                                     |
| [Squeeze](https://en.wikipedia.org/wiki/Squeeze_(file_format)) | `.sqz`        | Yes  | Yes | [Squeeze format](http://fileformats.archiveteam.org/wiki/SQ)                                            |
| PowerPacker                                               | `.pp`              | Yes  | Yes | [Amiga PP20](http://fileformats.archiveteam.org/wiki/Powerpacker)                                       |
| ICE Packer                                                | `.ice`             | Yes  | Yes | [Atari ST ICE](http://fileformats.archiveteam.org/wiki/ICE)                                             |
| [PackBits](https://en.wikipedia.org/wiki/PackBits)        | `.packbits`        | Yes  | Yes | [Apple PackBits](https://en.wikipedia.org/wiki/PackBits)                                                |
| Yaz0 (SZS)                                                | `.yaz0`,`.szs`     | Yes  | Yes | [Nintendo Yaz0 RE](https://wiki.tockdom.com/wiki/YAZ0)                                                 |
| BriefLZ                                                   | `.blz`             | Yes  | Yes | [BriefLZ source](https://github.com/jibsen/brieflz)                                                    |
| RNC                                                       | `.rnc`             | Yes  | Yes | [Rob Northen RE](http://segaretro.org/Rob_Northen_compression)                                         |
| RefPack / QFS                                             | `.qfs`,`.refpack`  | Yes  | Yes | [RefPack RE](http://wiki.niotso.org/RefPack)                                                           |
| aPLib                                                     | `.aplib`           | Yes  | Yes | [aPLib docs](http://ibsensoftware.com/products_aPLib.html)                                             |
| [LZFSE](https://en.wikipedia.org/wiki/LZFSE)              | `.lzfse`           | Yes  | Yes | [Apple LZFSE source](https://github.com/lzfse/lzfse)                                                   |
| Freeze                                                    | `.f`,`.freeze`     | Yes  | Yes | [Unix Freeze](http://fileformats.archiveteam.org/wiki/Freeze)                                          |
| [uuencoding](https://en.wikipedia.org/wiki/Uuencoding)    | `.uu`,`.uue`       | Yes  | Yes | [POSIX uuencode](https://pubs.opengroup.org/onlinepubs/9699919799/utilities/uuencode.html)             |
| [yEnc](https://en.wikipedia.org/wiki/YEnc)                | `.yenc`            | Yes  | Yes | [yEnc spec](http://www.yenc.org/yenc-draft.1.3.txt)                                                    |
| Density                                                   | `.density`         | Yes  | Yes | [Density source](https://github.com/k0dai/density)                                                     |
| LZG                                                       | `.lzg`             | Yes  | Yes | [LZG source](https://github.com/mbitsnbites/liblzg)                                                    |
| BCM                                                       | `.bcm`             | Yes  | Yes | [BCM source](https://github.com/encode84/bcm)                                                          |
| BSC                                                       | `.bsc`             | Yes  | Yes | [libbsc](https://github.com/IlyaGrebnov/libbsc)                                                        |
| BALZ                                                      | `.balz`            | Yes  | Yes | [BALZ source](https://sourceforge.net/projects/balz/)                                                  |
| CSC                                                       | `.csc`             | Yes  | Yes | [CSC source](https://github.com/fusiyuan2010/CSC)                                                      |
| Zling                                                     | `.zling`           | Yes  | Yes | [libzling](https://github.com/richox/libzling)                                                         |
| [Lizard](https://github.com/inikep/lizard)                | `.lizard`          | Yes  | Yes | [Lizard source](https://github.com/inikep/lizard)                                                      |
| QuickLZ                                                   | `.quicklz`         | Yes  | Yes | [QuickLZ docs](http://www.quicklz.com/)                                                                |
| [cmix](https://www.byronknoll.com/cmix.html)              | `.cmix`            | Yes  | Yes | [cmix source](https://github.com/byronknoll/cmix)                                                      |
| MCM                                                       | `.mcm`             | Yes  | Yes | [MCM source](https://github.com/mathieuchartier/mcm)                                                   |
| [PAQ8](https://en.wikipedia.org/wiki/PAQ)                 | `.paq8`            | Yes  | Yes | [Matt Mahoney PAQ page](https://mattmahoney.net/dc/)                                                   |
| [SWF](https://en.wikipedia.org/wiki/SWF)                  | `.swf`             | Yes  | Yes | [SWF 19 spec](https://open-flash.github.io/mirrors/swf-spec-19.pdf)                                    |
| CP/M Crunch                                               | `.cru`             | Yes  | Yes | [CP/M CRUNCH](http://www.retroarchive.org/docs/cpm.html)                                               |
| [PPMd](https://en.wikipedia.org/wiki/Prediction_by_partial_matching) | `.pmd`   | Yes  | Yes | [Shkarin PPMd](https://github.com/jk-jeon/PPMd)                                                        |
| LZHAM                                                     | `.lzham`           | Yes  | Yes | [LZHAM source](https://github.com/richgel999/lzham_codec)                                              |
| LZS                                                       | `.lzs`             | Yes  | Yes | [RFC 1967](https://www.rfc-editor.org/rfc/rfc1967) / [RFC 2395](https://www.rfc-editor.org/rfc/rfc2395)|
| [FLAC](https://en.wikipedia.org/wiki/FLAC)                | `.flac`            | Yes  | Yes | [FLAC format](https://xiph.org/flac/format.html)                                                       |

### Compound formats

`tar.gz`, `tar.bz2`, `tar.xz`, `tar.zst`, `tar.lz4`, `tar.lz`, `tar.br` — auto-detected, both read and write.

### Modern packaging

| Format    | Extensions              | Read | Write | Reference                                                                                                         | Notes                                                                               |
| --------- | ----------------------- | ---- | ----- | ----------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| [AppImage](https://en.wikipedia.org/wiki/AppImage)  | `.AppImage`           | Yes | - | [AppImage spec](https://github.com/AppImage/AppImageSpec)                                                         | ELF stub + appended SquashFS; offset located by ELF section-end + magic scan         |
| [Snap](https://en.wikipedia.org/wiki/Snap_(software)) | `.snap`            | Yes | - | [snapd source](https://github.com/snapcore/snapd)                                                                 | SquashFS with `meta/snap.yaml`                                                       |
| [MSIX](https://en.wikipedia.org/wiki/MSIX)         | `.msix`,`.msixbundle` | Yes | - | [MSIX spec](https://learn.microsoft.com/en-us/windows/msix/)                                                      | Modern Windows app package (mirrors APPX)                                            |
| ESD                                                | `.esd`                | Yes | - | [WIM/ESD overview](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wim-and-esd-windows-image-files-overview) | Windows Update encrypted-LZMS WIM; shares `MSWIM\0\0\0` magic, extension-only |
| Split WIM                                          | `.swm`,`.swmN`        | Yes | - | [WIM spec](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/)                               | Multi-part WIM volume                                                                |
| [WACZ](https://specs.webrecorder.net/wacz/1.0.0/)  | `.wacz`               | Yes | - | [WACZ 1.0.0](https://specs.webrecorder.net/wacz/1.0.0/)                                                           | Web Archive Collection Zipped — ZIP around WARC + `datapackage.json`                 |
| [Python Wheel](https://en.wikipedia.org/wiki/Wheel_(software)) | `.whl`     | Yes | - | [PEP 427](https://peps.python.org/pep-0427/)                                                                      | ZIP with `dist-info/METADATA`, `WHEEL`, `RECORD`                                     |
| [Ruby Gem](https://en.wikipedia.org/wiki/RubyGems) | `.gem`                | Yes | - | [gem spec](https://guides.rubygems.org/specification-reference/)                                                  | TAR with `metadata.gz`, `data.tar.gz`, `checksums.yaml.gz`                           |
| Rust Crate                                         | `.crate`              | Yes | - | [cargo spec](https://doc.rust-lang.org/cargo/reference/registries.html)                                           | TAR.GZ with single `name-version/` directory containing `Cargo.toml`                 |

### Firmware and embedded

| Format              | Extensions                           | Read | Write | Reference                                                                                                         | Notes                                                                 |
| ------------------- | ------------------------------------ | ---- | ----- | ----------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------- |
| [U-Boot uImage](https://en.wikipedia.org/wiki/Das_U-Boot) | `.uimg`,`.img`,`.bin`          | Yes | - | [U-Boot image.h](https://github.com/u-boot/u-boot/blob/master/include/image.h)                                    | 64-byte legacy header + body; reports OS/arch/comp; decompresses payload when possible |
| [Device Tree Blob](https://en.wikipedia.org/wiki/Devicetree) | `.dtb`,`.dtbo`              | Yes | - | [DT spec](https://www.devicetree.org/specifications/)                                                             | FDT v17, walks property tree as pseudo-archive                        |
| [Intel HEX](https://en.wikipedia.org/wiki/Intel_HEX)         | `.hex`,`.ihex`              | Yes | - | [Intel HEX spec](https://developer.arm.com/documentation/ka003292/latest)                                         | ASCII firmware records, decoded to flat `firmware.bin` + metadata     |
| [Motorola S-Record](https://en.wikipedia.org/wiki/SREC_(file_format)) | `.s19`,`.s28`,`.s37`,`.srec`,`.mot` | Yes | - | [SREC spec](https://www.nxp.com/docs/en/reference-manual/M68HC11RM.pdf)                          | 16/24/32-bit address records                                          |
| TI-TXT                                                       | -                           | Yes | - | [MSP430 programming](https://www.ti.com/lit/an/slau319af/slau319af.pdf)                                           | MSP430 firmware text, address blocks                                  |
| UEFI Firmware Volume                                         | `.fv`,`.fd`,`.rom`,`.bin`   | Yes | - | [UEFI PI vol.3](https://uefi.org/specifications)                                                                  | `_FVH` at offset 40, walks FFS files                                   |

### Disk-image + forensics

| Format | Extensions            | Read | Write | Reference                                                                                                            | Notes                                                                                       |
| ------ | --------------------- | ---- | ----- | -------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| [VHDX](https://en.wikipedia.org/wiki/VHD_(file_format)#VHDX) | `.vhdx`         | Yes | - | [MS VHDX spec](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-vhdx/)                              | Hyper-V modern; surfaces File Type ID + 2 headers + 2 region tables (BAT walk deferred)     |
| [EWF/E01](https://en.wikipedia.org/wiki/Expert_Witness_Compression_Format) | `.e01`,`.ewf`,`.l01` | Yes | - | [libewf docs](https://github.com/libyal/libewf)                                                    | EnCase forensic image; section-chain walker, header2 + MD5/SHA1                             |
| [G64](https://en.wikipedia.org/wiki/Commodore_1541)          | `.g64`          | Yes | - | [VICE G64 docs](https://vice-emu.sourceforge.io/vice_17.html)                                                      | Commodore GCR-encoded track dump (1541)                                                     |
| NIB                                                          | `.nib`          | Yes | - | [nibtools docs](http://c64preservation.com/nibtools)                                                                | Commodore raw nibble track dump                                                             |

### Scientific and ML

| Format              | Extensions         | Read | Write | Reference                                                                                                       | Notes                                                                          |
| ------------------- | ------------------ | ---- | ----- | --------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------ |
| [NumPy NPY](https://en.wikipedia.org/wiki/NumPy)            | `.npy`          | Yes | - | [NEP 1 / npy-format](https://numpy.org/neps/nep-0001-npy-format.html)                                           | Single ndarray header + raw bytes                                              |
| NumPy NPZ                                                   | `.npz`          | Yes | - | [savez docs](https://numpy.org/doc/stable/reference/generated/numpy.savez.html)                                 | ZIP of NPYs                                                                    |
| [NIfTI-1/2](https://en.wikipedia.org/wiki/Neuroimaging_Informatics_Technology_Initiative) | `.nii`,`.nii.gz` | Yes | - | [NIfTI spec](https://nifti.nimh.nih.gov/nifti-1/documentation)                            | Medical imaging (MRI); 352-byte v1 / 540-byte v2 header + voxel data; transparent gzip |
| [HDF4](https://en.wikipedia.org/wiki/Hierarchical_Data_Format#HDF4) | `.hdf`,`.hdf4`,`.h4` | Yes | - | [HDF4 reference](https://support.hdfgroup.org/release4/doc/)                                       | DD linked-list walker, tag histogram + per-DD entry                            |
| [ONNX](https://en.wikipedia.org/wiki/Open_Neural_Network_Exchange) | `.onnx`  | Yes | - | [ONNX proto](https://github.com/onnx/onnx/blob/main/onnx/onnx.proto)                                             | Pure-C# protobuf reader; surfaces graph initializers as entries                |

### CAD / 3D

| Format  | Extensions | Read | Write | Reference                                                                                                  | Notes                                                                       |
| ------- | ---------- | ---- | ----- | ---------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------- |
| [STL](https://en.wikipedia.org/wiki/STL_(file_format))   | `.stl`    | Yes | - | [STL spec](http://www.ennex.com/~fabbers/StL.asp)                                                                       | ASCII + binary; triangle count, bounding box, name                          |
| [PLY](https://en.wikipedia.org/wiki/PLY_(file_format))   | `.ply`    | Yes | - | [Stanford PLY](http://paulbourke.net/dataformats/ply/)                                                                  | ASCII / binary LE/BE, element schema                                        |
| [DXF](https://en.wikipedia.org/wiki/AutoCAD_DXF)         | `.dxf`    | Yes | - | [Autodesk DXF ref](https://help.autodesk.com/view/OARX/2022/ENU/?guid=GUID-235B22E0-A567-4CF6-92D3-38A2306D73F3)        | AutoCAD ASCII; section list + entity histogram                              |
| [Collada](https://en.wikipedia.org/wiki/COLLADA)         | `.dae`    | Yes | - | [Khronos Collada 1.5](https://www.khronos.org/files/collada_spec_1_5.pdf)                                               | XML 3D interchange                                                          |
| [3DS](https://en.wikipedia.org/wiki/.3ds)                | `.3ds`    | Yes | - | [lib3ds docs](http://lib3ds.sourceforge.net/)                                                                            | Autodesk binary chunks                                                      |

### Medical imaging

| Format   | Extensions           | Read | Write | Reference                                                                         | Notes                                                                |
| -------- | -------------------- | ---- | ----- | --------------------------------------------------------------------------------- | -------------------------------------------------------------------- |
| [DICOM](https://en.wikipedia.org/wiki/DICOM)   | `.dcm`              | Yes | - | [NEMA DICOM PS3](https://www.dicomstandard.org/current)                     | Single DICOM image                                                    |
| DICOMDIR                                       | `.dcmdir`,`DICOMDIR` | Yes | - | [DICOM PS3.10](https://dicom.nema.org/medical/dicom/current/output/chtml/part10/) | Multi-study patient/series index referencing sibling DICOM files     |

### Streaming and subtitle

| Format                    | Extensions            | Read | Write | Reference                                                                                                    | Notes                                                                              |
| ------------------------- | --------------------- | ---- | ----- | ------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------- |
| [SUP (PGS)](https://en.wikipedia.org/wiki/Presentation_Graphic_Stream) | `.sup`   | Yes | - | [PGS RE doc](https://blog.thescorpius.com/index.php/2017/07/15/presentation-graphic-stream-sup-files-bluray-subtitle-format/) | Blu-ray Presentation Graphic Stream subtitle segments, grouped by epoch    |
| [VobSub](https://en.wikipedia.org/wiki/VobSub)                        | `.idx` + `.sub` | Yes | - | [MPlayer vobsub](https://www.mplayerhq.hu/DOCS/tech/mpsub.sub)                                                  | DVD subtitle pair; parses `.idx` palette/timestamps + slices sibling `.sub` PES |
| [HLS M3U8](https://en.wikipedia.org/wiki/HTTP_Live_Streaming)         | `.m3u8`,`.m3u`  | Yes | - | [RFC 8216](https://www.rfc-editor.org/rfc/rfc8216)                                                              | HTTP Live Streaming manifest                                                       |
| [MPEG-TS](https://en.wikipedia.org/wiki/MPEG_transport_stream)        | `.ts`,`.m2ts`,`.mts` | Yes | - | [ITU-T H.222.0](https://www.itu.int/rec/T-REC-H.222.0)                                                       | MPEG-2 Transport Stream demuxed into per-PID elementary streams                    |

### Audio codecs

Standalone audio codecs live under `Codec.*` projects (separate from container-format descriptors). Each exposes a static `Decompress(Stream input, Stream output)` producing interleaved little-endian PCM and a `ReadStreamInfo` for metadata-only access. Encoders are explicitly out of scope for the new codecs — only the legacy ones ship encoders.

| Codec     | Project          | Encoder | Decoder state                                                                                                                                                                                                         | Reference                                                                 |
| --------- | ---------------- | ------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------- |
| [PCM](https://en.wikipedia.org/wiki/Pulse-code_modulation)       | `Codec.Pcm`      | Yes | Production — raw integer PCM up to 32-bit                                                                                                                              | —                                                                         |
| [FLAC](https://en.wikipedia.org/wiki/FLAC)                       | `Codec.Flac`     | Yes | Production — FIXED + LPC subframes, all sample rates / bit depths                                                                                                      | [xiph.org/flac](https://xiph.org/flac/format.html)                        |
| [A-law](https://en.wikipedia.org/wiki/A-law_algorithm)           | `Codec.ALaw`     | Yes | Production — G.711                                                                                                                                                      | [ITU-T G.711](https://www.itu.int/rec/T-REC-G.711)                        |
| [μ-law](https://en.wikipedia.org/wiki/%CE%9C-law_algorithm)      | `Codec.MuLaw`    | Yes | Production — G.711                                                                                                                                                      | [ITU-T G.711](https://www.itu.int/rec/T-REC-G.711)                        |
| [GSM 06.10](https://en.wikipedia.org/wiki/Full_Rate)             | `Codec.Gsm610`   | Yes | Production — full RPE-LTP                                                                                                                                               | [ETSI GSM 06.10](https://www.etsi.org/deliver/etsi_gts/06/0610/03.02.00_60/gsmts_0610sv030200p.pdf) |
| [IMA ADPCM](https://en.wikipedia.org/wiki/Interactive_Multimedia_Association) | `Codec.ImaAdpcm` | Yes | Production — Microsoft + Apple variants                                                                                                         | [IMA ADPCM spec](http://www.cs.columbia.edu/~hgs/audio/dvi/IMA_ADPCM.pdf) |
| MS ADPCM                                                         | `Codec.MsAdpcm`  | Yes | Production — WAV format 0x0002                                                                                                                                           | [MS ADPCM spec](https://wiki.multimedia.cx/index.php/Microsoft_ADPCM)     |
| [MIDI](https://en.wikipedia.org/wiki/MIDI)                       | `Codec.Midi`     | Yes | Production — SMF 0/1/2 with all standard meta + channel events                                                                                                          | [MIDI 1.0 spec](https://www.midi.org/specifications-old/item/the-midi-1-0-specification) |
| [**MP3**](https://en.wikipedia.org/wiki/MP3)                     | `Codec.Mp3`      | -   | **Header + framing complete; bit-exact decode unverified** — minimp3 port (1469 LOC, scalar) covering MPEG-1/2/2.5 Layer III, MS+intensity stereo, ID3v2 skip, Xing VBR. Layer I/II rejection passes. End-to-end PCM decode against a reference clip is deferred until an MP3 test vector lands in `test-corpus/`. | [ISO/IEC 11172-3](https://www.iso.org/standard/22412.html) / [minimp3](https://github.com/lieff/minimp3) |
| [**Vorbis**](https://en.wikipedia.org/wiki/Vorbis)               | `Codec.Vorbis`   | -   | **Partial** — stb_vorbis structural port (1295 LOC) covering Ogg page reassembly, codebooks (lookup 0/1/2), floor 1, residue 0/1/2, channel coupling, IMDCT. Floor 0 throws `NotSupportedException`. End-to-end test marked `Inconclusive` until a test vector lands in `test-corpus/`. | [Vorbis I spec](https://xiph.org/vorbis/doc/Vorbis_I_spec.html)           |
| [**Opus**](https://en.wikipedia.org/wiki/Opus_(audio_format))    | `Codec.Opus`     | -   | **Framing only** — Ogg page walker + OpusHead/OpusTags + TOC byte + frame packing modes 0/1/2/3 + range decoder (`ec_dec`) all real. CELT and SILK pipelines are stubs that emit silence at the correct sample count. Hybrid mode throws `NotSupportedException`. | [RFC 6716](https://www.rfc-editor.org/rfc/rfc6716)                        |
| [**AAC-LC**](https://en.wikipedia.org/wiki/Advanced_Audio_Coding) | `Codec.Aac`     | -   | **Framing only** — ADTS frame parser + AudioSpecificConfig + element dispatcher + profile gating real. Spectral pipeline + Huffman tables + IMDCT + filterbank scaffolded but spectral data tables are TODO. HE-AAC v1/v2 + Main/SSR/LTP/ER all throw `NotSupportedException`. | [ISO/IEC 14496-3](https://www.iso.org/standard/76383.html)                |

**Implementation philosophy.** The four new audio codecs (MP3 / Vorbis / Opus / AAC-LC) ship under the project's "no toy implementations" rule — partial state is documented openly (in class summaries, in `Assert.Ignore` messages, and in this table) rather than silently producing wrong PCM. Future work: bit-pack debugging for MP3, real CELT/SILK for Opus, spectral table population for AAC, reference test-vector validation across all four.

### Known partial implementations

Code paths that throw `NotSupportedException` or `NotImplementedException` rather than silently producing wrong output. Documented here so expectations match behaviour.

| Area                              | State                                                                                                                                                                                                                      |
| --------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| MP3 / Vorbis / Opus / AAC-LC      | Partial decoder state — see Audio Codecs table. MP3 bit-exact needs test vectors; Vorbis floor 0 throws (obsolete since 2004 — stb_vorbis doesn't implement it either); Opus CELT/SILK + AAC spectral filterbank are multi-week DSP projects |
| LZFSE V1 / V2 blocks              | FSE/tANS backend not implemented — uncompressed (`bvxn`) + LZVN blocks work. Full LZFSE needs ~1500 LOC new code (Apple reference impl)                                                                                   |
| ZPAQ                              | Reader requires a ZPAQL virtual machine (not implemented). Multi-week bytecode-VM project                                                                                                                                  |
| StuffIt X writer                  | Proprietary element-catalog / P2-varint writer not implemented — WORM emits valid `StuffIt!` envelope shell. No public spec, only reverse-engineering notes                                                                |
| UMX writer                        | Full export table + compact-index music encoding not implemented — WORM emits valid header only                                                                                                                            |
| OLE2 application streams (DOC/XLS/PPT/MSG/ThumbsDb/MSI) | CFB envelope round-trips through our reader and libgsf/Apache POI, but the internal `WordDocument` / `WorkBook` / `PowerPoint Document` / MAPI / Catalog / Installer-DB streams are not synthesised. Each is a 400+ page MS Open Specification |
| Inno Setup reader                 | Individual file extraction from `Setup.1` not implemented for some installer versions                                                                                                                                      |
| EROFS                             | Compressed layouts (LZ4/LZMA-compressed inodes) not decompressed — plain-storage inodes work                                                                                                                               |
| ExtRemover                        | Indirect-block traversal not implemented for file removal (direct blocks work)                                                                                                                                             |
| F2FS writer                       | Indirect-block allocation not implemented — per-file max ≈ 3.6 MB (923 direct pointers in inode, no direct_node/indirect_node chain)                                                                                       |
| RAR create                        | Only v4 and v5 archive creation are implemented                                                                                                                                                                            |

**Fixes landed in this pass** (documented here so the list above is what's actually pending):

| Area                    | Before                                                        | After                                                                                                                                                                                                                  |
| ----------------------- | ------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **CAB LZX**             | Enum marked "(not implemented)"                               | Comment was stale — reader (`LzxDecompressor`) and writer (`BB_Lzx`) were already wired                                                                                                                                |
| **MPQ bzip2** (method 0x10) | Returned payload raw                                       | Now invokes `Bzip2Stream` on the payload, falls back to raw on decode failure                                                                                                                                          |
| **FAT32 writer**        | Threw `NotSupportedException` for images ≥ 65525 clusters     | Full FATGEN103-compliant FAT32: extended BPB (`BPB_RootClus`/`BPB_FSInfo`/`BPB_BkBootSec`), FSInfo sector with the three canonical signatures, backup boot sector at sector 6, cluster-2 root directory with EoC marker, 32 reserved sectors, FS-type string `FAT32   ` |
| **ProDOS tree storage** | Files > 128 KB rejected outright                              | Writer emits storage-type-3 trees: master index block + up to 256 subordinate index blocks → 32 MB per file. Reader already handled type 3                                                                             |

### Filesystem images

Snapshot: 41 filesystems, 37 read+write, 4 read-only. Spec = the external document/source the writer was validated against.

#### Windows / DOS native

| FS                              | State | Spec                                                                                               | Notes                                                                                                                                     |
| ------------------------------- | ----- | -------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| [FAT12/16/32](https://en.wikipedia.org/wiki/File_Allocation_Table)    | R/W | [Microsoft FATGEN](https://download.microsoft.com/download/1/6/1/161ba512-40e2-4cc9-843a-923143f3456c/fatgen103.doc) | Full BPB, `0x55 0xAA` signature, auto-select FAT12/16/32 by cluster count. FAT32 includes extended BPB, FSInfo sector + backup boot sector + cluster-2 root directory |
| [exFAT](https://en.wikipedia.org/wiki/ExFAT)                          | R/W | [Microsoft exFAT spec](https://learn.microsoft.com/en-us/windows/win32/fileio/exfat-specification) | Full VBR, boot-checksum sector (§3.1.3), Upcase/Bitmap/VolumeLabel root entries                                                            |
| [NTFS](https://en.wikipedia.org/wiki/NTFS)                            | R/W | [MS-NT on-disk](https://learn.microsoft.com/en-us/windows-server/storage/file-server/ntfs-overview) / [TSK docs](https://wiki.sleuthkit.org/index.php?title=NTFS) | All 16 system MFT files, USA fixup, LZNT1 compression                                       |
| [DoubleSpace / DriveSpace CVF](https://en.wikipedia.org/wiki/Microsoft_DoubleSpace) | R/W | [MS-DOS 6 Technical Reference](http://www.os2museum.com/wp/doublespace-and-drivespace-internals/) | Full MDBPB with DBLS/DVRS signature, MDFAT + BitFAT, inner FAT12/16 with VFAT LFN. Stored runs only (JM/LZ77 is TODO)                     |
| [HPFS](https://en.wikipedia.org/wiki/High_Performance_File_System)    | RO  | [OS/2 Inside Story](https://www.edm2.com/0401/hpfs.html)                                           | Read-only descriptor (no writer)                                                                                                           |

#### Unix / Linux

| FS                                                        | State | Spec                                                                  | Notes                                                                                                                                                              |
| --------------------------------------------------------- | ----- | --------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| [ext2/3/4](https://en.wikipedia.org/wiki/Ext4)            | R/W | [Linux kernel `fs/ext2/ext2.h`](https://github.com/torvalds/linux/blob/master/fs/ext2/ext2.h) | Spec-compliant, random UUID; FS revision 0 GOOD_OLD                                                                                                                |
| [XFS v5](https://en.wikipedia.org/wiki/XFS)               | R/W | [Linux `fs/xfs/libxfs/xfs_format.h`](https://github.com/torvalds/linux/blob/master/fs/xfs/libxfs/xfs_format.h) | v5 with v3 dinodes, `sb_crc` CRC-32C, `sb_features_*`/`sb_meta_uuid`/`sb_pquotino`                                                     |
| [JFS](https://en.wikipedia.org/wiki/JFS_(file_system))    | R/W | [Linux `fs/jfs/jfs_superblock.h`](https://github.com/torvalds/linux/blob/master/fs/jfs/jfs_superblock.h) | pxd_t bit-packing (24-bit length + 40-bit address), inline dtree root, aggregate inode table with FILESYSTEM_I=16                |
| [ReiserFS 3.6](https://en.wikipedia.org/wiki/ReiserFS)    | R/W | [Linux `fs/reiserfs/reiserfs.h`](https://github.com/torvalds/linux/blob/master/fs/reiserfs/reiserfs.h) | Spec-correct offsets, `ReIsEr2Fs` @+52, leaf block-head. No block CRC (v3.6 doesn't have them)                                    |
| [F2FS](https://en.wikipedia.org/wiki/F2FS)                | R/W | [Linux `include/linux/f2fs_fs.h`](https://github.com/torvalds/linux/blob/master/include/linux/f2fs_fs.h) | Superblock magic at block-offset 0x400, CP + SIT + NAT + SSA + Main, CRC-32C, inline dentries in root inode                        |
| [Btrfs](https://en.wikipedia.org/wiki/Btrfs)              | R/W | [Btrfs on-disk format](https://btrfs.wiki.kernel.org/index.php/On-disk_Format) | Real chunk tree (SYSTEM/METADATA/DATA), `sys_chunk_array` in superblock, DEV_ITEM, CRC-32C on every block header             |
| [ZFS](https://en.wikipedia.org/wiki/ZFS)                  | R/W | [OpenZFS source](https://github.com/openzfs/zfs)                      | 4 vdev labels, 128-entry uberblock ring with Fletcher-4, XDR NVList, MOS + DSL dir/dataset + microzap for root, pool version 28                                     |
| [UFS1/FFS](https://en.wikipedia.org/wiki/Unix_File_System) | R/W | [FreeBSD `sys/ufs/ffs/fs.h`](https://github.com/freebsd/freebsd-src/blob/main/sys/ufs/ffs/fs.h) | `fs_magic=0x011954` at sb offset 1372, cg_magic, `fs_cs` summary block                                                              |
| [UBIFS](https://en.wikipedia.org/wiki/UBIFS)              | RO  | [Linux `fs/ubifs/`](https://github.com/torvalds/linux/tree/master/fs/ubifs) | Read-only; no writer (LPT/TNC trees are multi-week)                                                                                                           |
| [JFFS2](https://en.wikipedia.org/wiki/JFFS2)              | RO  | [Linux `fs/jffs2/`](https://github.com/torvalds/linux/tree/master/fs/jffs2) | Read-only; log-structured node-scanner only                                                                                                                   |
| [YAFFS2](https://en.wikipedia.org/wiki/YAFFS)             | RO  | [Aleph One YAFFS2 spec](https://yaffs.net/yaffs-2-specification)      | Read-only; OOB/ECC layout not emittable                                                                                                                             |
| [BFS (BeOS/Haiku)](https://en.wikipedia.org/wiki/Be_File_System) | RO  | [Haiku OS source](https://github.com/haiku/haiku)               | Read-only; superblock surfacing only                                                                                                                                |

#### Apple / classic Mac

| FS                                                            | State | Spec                                                                  | Notes                                                                                                                                    |
| ------------------------------------------------------------- | ----- | --------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| [HFS classic](https://en.wikipedia.org/wiki/Hierarchical_File_System) | R/W | Apple "Inside Macintosh: Files" (1992)                        | Real B-tree catalog + extents trees, 102-byte file records, 70-byte dir records, 46-byte thread records with (parent, name) sort        |
| [HFS+](https://en.wikipedia.org/wiki/HFS_Plus)                | R/W | [Apple TN1150](https://developer.apple.com/library/archive/technotes/tn/tn1150.html) | Catalog file record at spec 248 bytes; dataFork @ 88, resourceFork @ 168                                                   |
| [APFS](https://en.wikipedia.org/wiki/Apple_File_System)       | R/W | [Apple File System Reference](https://developer.apple.com/support/downloads/Apple-File-System-Reference.pdf) | NX superblock + container OMAP + APSB volume + FS B-tree, Fletcher-64 checksums, single-container WORM            |
| [MFS](https://en.wikipedia.org/wiki/Macintosh_File_System)    | R/W | Inside Macintosh V (1985)                                             | Pre-HFS flat FS; `drSigWord=0xD2D7`                                                                                                       |

#### Retro / 8-bit

| FS                                                        | State | Spec                                                                  | Notes                                                                                                 |
| --------------------------------------------------------- | ----- | --------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| [Commodore 1541 (.d64)](https://en.wikipedia.org/wiki/Commodore_1541) | R/W | [VICE emulator docs](https://vice-emu.sourceforge.io/vice_17.html) | 174 848 bytes, 35 tracks, directory at T18S1+                                                          |
| Commodore 1571 (.d71)                                     | R/W | VICE docs                                                             | 349 696 bytes, dual-side BAM                                                                            |
| Commodore 1581 (.d81)                                     | R/W | VICE docs                                                             | 819 200 bytes, 80 × 40 × 256, DOS "3D" signature                                                        |
| C64 tape (.t64)                                           | R/W | [T64 format spec](http://unusedino.de/ec64/technical/formats/t64.html)| "C64S tape image file" header                                                                           |
| [Amiga ADF (OFS/FFS)](https://en.wikipedia.org/wiki/Amiga_Disk_File) | R/W | [Amiga ROS docs](http://lclevy.free.fr/adflib/adf_info.html) | 901 120 (DD) / 1 802 240 (HD), "DOS\1" magic, BSDsum checksums                                                 |
| Amiga DMS                                                 | R/W | [xDMS source](https://github.com/markrabjohn/xDMS)                    | "DMS!" header with CRC16                                                                                |
| Atari ST MSA                                              | R/W | [MSA format spec](http://info-coach.fr/atari/documents/_mydoc/FD_image_file_formats.pdf) | 0x0E0F BE magic, per-track RLE                                                         |
| Atari 8-bit ATR                                           | R/W | [AtariDOS 2 VTOC](http://www.atariarchives.org/dere/chapt09.php)      | 16-byte header + 92 160 sector bytes, VTOC @ sector 360                                                  |
| Apple DOS 3.3                                             | R/W | [Apple DOS manual](https://archive.org/details/BeneaththeAppleDOS)    | 143 360 bytes, catalog at T17S15 chain, 35-byte entries                                                  |
| ProDOS                                                    | R/W | [ProDOS TRM](https://archive.org/details/prodos8_technical_reference_manual) | 143 360 (5.25") / 819 200 (800K), 39-byte entries                                                |
| BBC Micro DFS (.ssd)                                      | R/W | [Acorn DFS spec](https://beebwiki.mdfs.net/Acorn_DFS_disc_format)     | 102 400 (40-track) / 204 800 (80-track), 31×8-byte dir entries                                           |
| [ZX Spectrum SCL](https://en.wikipedia.org/wiki/TR-DOS)   | R/W | [TR-DOS .scl spec](https://sinclair.wiki.zxnet.co.uk/wiki/SCL_format) | "SINCLAIR" magic + LE32 trailing sum                                                                    |
| ZX Spectrum TR-DOS (.trd)                                 | R/W | [TR-DOS spec](https://sinclair.wiki.zxnet.co.uk/wiki/TR-DOS_filesystem) | 655 360 bytes, 160×16×256                                                                               |
| Amstrad CPC DSK                                           | R/W | [CPCEMU disk format](https://www.cpcwiki.eu/index.php/Format:DSK_disk_image_file_format) | "MV - CPCEMU Disk-File" magic                                                       |
| HP LIF (.lif)                                             | R/W | HP LIF utility manual                                                 | 256-byte sectors, flat directory, 0x8000 BE magic                                                       |
| [CP/M 2.2](https://en.wikipedia.org/wiki/CP/M)            | R/W | [DR CP/M 2.2 BDOS reference](https://www.cpm.z80.de/manuals/dri_cpm22.pdf) | 256 256 bytes (77×26×128), 64-entry flat directory                                                  |
| DEC RT-11 (.rt11/.rx01)                                   | R/W | [DEC RT-11 Volume + File Formats](https://www.dbit.com/pub/pdp11/rt11/)| RX01 8" SSSD ~256 KB, 512-byte blocks, RAD-50 6.3 names                                                 |
| OS-9 RBF (.os9/.rbf)                                      | R/W | Microware OS-9 Tech Reference                                         | CoCo 35-track DSDD ~315 KB, 256-byte sectors, big-endian                                                |
| Commodore G64 (.g64)                                      | RO  | [VICE emulator docs](https://vice-emu.sourceforge.io/vice_17.html)    | GCR-encoded track dump; raw GCR bytes per track                                                         |
| Commodore NIB (.nib)                                      | RO  | [nibtools docs](http://c64preservation.com/nibtools)                  | Raw 84-half-track nibble dump                                                                           |

#### Optical

| FS                                                 | State | Spec                                                                      | Notes                                                                                                                                                                                           |
| -------------------------------------------------- | ----- | ------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [ISO 9660](https://en.wikipedia.org/wiki/ISO_9660) | R/W | [ECMA-119](https://ecma-international.org/publications-and-standards/standards/ecma-119/) | PVD at sector 16, VDST @ 17, L+M path tables, 2 KB blocks, flat directory (no Rock Ridge/Joliet)           |
| [UDF](https://en.wikipedia.org/wiki/Universal_Disk_Format) | R/W | [ECMA-167](https://ecma-international.org/publications-and-standards/standards/ecma-167/) | VRS (BEA01/NSR02/TEA01) @ 16-18, Main VDS @ 32-35, AVDP @ 256. CRC-16-XMODEM + TagChecksum on every tag |

#### Embedded / flash

| FS                                                     | State | Spec                                                                  | Notes                                                      |
| ------------------------------------------------------ | ----- | --------------------------------------------------------------------- | ---------------------------------------------------------- |
| [SquashFs](https://en.wikipedia.org/wiki/SquashFS)     | R/W | [SquashFs 4.0 spec](https://dr-emann.github.io/squashfs/)             | `hsqs` magic, zlib + Adler-32, FlagNoFragments             |
| [CramFs](https://en.wikipedia.org/wiki/Cramfs)         | R/W | [Linux `fs/cramfs/`](https://github.com/torvalds/linux/tree/master/fs/cramfs) | `0x28CD3D45` magic, CRC-32, zlib blocks             |
| [RomFs](https://en.wikipedia.org/wiki/Romfs)           | R/W | [Linux `fs/romfs/`](https://github.com/torvalds/linux/tree/master/fs/romfs) | `-rom1fs-` magic, BE fields, self-correcting checksum |
| [EROFS](https://en.wikipedia.org/wiki/EROFS)           | RO  | [Linux `fs/erofs/`](https://github.com/torvalds/linux/tree/master/fs/erofs) | Read-only; variable-length encoded inodes                   |
| [Minix v1/2/3](https://en.wikipedia.org/wiki/MINIX_file_system) | R/W | [Linux `fs/minix/`](https://github.com/torvalds/linux/tree/master/fs/minix) | Superblock magics `0x137F/0x138F/0x2468/0x2478/0x4D5A` |
| VDFS                                                   | R/W (writer toy) | Gothic-game engine reverse engineering                     | Proprietary, no public spec; writer is flat-no-checksum (left in place until spec available) |

### Disk- and disc-image containers

Containers holding filesystem payloads; the inner FS is a separate descriptor.

| Format                                                    | State | Reference                                                                          | Notes                                                                    |
| --------------------------------------------------------- | ----- | ---------------------------------------------------------------------------------- | ------------------------------------------------------------------------ |
| [VHD (Microsoft)](https://en.wikipedia.org/wiki/VHD_(file_format)) | R/W | [VHD format spec](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2003/cc708289(v=ws.10)) | "conectix" magic, fixed + dynamic                        |
| [VMDK (VMware)](https://en.wikipedia.org/wiki/VMDK)       | R/W | [VMDK spec](https://www.vmware.com/app/vmdk/?src=vmdk)                             | "KDMV" magic                                                              |
| [QCOW2 (QEMU)](https://en.wikipedia.org/wiki/Qcow)        | R/W | [QCOW2 docs](https://github.com/qemu/qemu/blob/master/docs/interop/qcow2.txt)      | Sparse format; WORM wraps raw disk with L1/L2, v2                          |
| [VDI (VirtualBox)](https://en.wikipedia.org/wiki/VDI_(file_format)) | R/W | [VBox source](https://github.com/mirror/vbox)                                | Single-disk format                                                        |
| BIN/CUE                                                   | R/W | [cue sheet](https://en.wikipedia.org/wiki/Cue_sheet_(computing))                   | Raw disc image; WORM emits ISO 9660 cooked sectors                         |
| [MDF](https://en.wikipedia.org/wiki/Alcohol_120%25)       | R/W | [Alcohol docs](https://en.wikipedia.org/wiki/Alcohol_120%25)                       | Alcohol 120% — WORM emits ISO 9660                                        |
| [NRG](https://en.wikipedia.org/wiki/NRG_(file_format))    | R/W | [Nero format docs](https://www.daemon-tools.cc/products/dtLite)                    | Nero — WORM emits ISO 9660 with NER5 footer                               |
| CDI                                                       | R/W | [DiscJuggler docs](https://en.wikipedia.org/wiki/DiscJuggler)                      | DiscJuggler — WORM emits ISO 9660 with CDI v2 footer                       |
| [DMG](https://en.wikipedia.org/wiki/Apple_Disk_Image)     | R/W | [libdmg-hfsplus](https://github.com/planetbeing/libdmg-hfsplus)                    | Apple disk image — WORM emits raw mish blocks per partition (no zlib/bz2/lzfse encoding); read-side handles all four compressions |

### Executable packers

CompressionWorkbench treats executable packers (UPX, demoscene compressors, classic DOS packers, modern PE protectors) as **pseudo-archives** — they get the same `List` / `Extract` interface as ZIP or TAR. Each descriptor surfaces a `metadata.ini` with detected evidence (version byte, signature offset, packer-header fields), an `mz_header.bin` / `hunk_header.bin` snapshot when applicable, and a `packed_payload.bin` (or an in-process decompressed body for UPX).

#### UPX (Ultimate Packer for eXecutables)

Detection is hardened against tampered binaries: the structural fingerprint (BSS-style first section + RWX flags + entry point in last section + payload entropy ≥ 7.5) catches binaries where the `UPX!` magic and section names have been wiped. A brute-force PackHeader scan validates `format` / `method` / `uLen` / `cLen` / `level` / `version` even when the magic bytes are zeroed.

| Capability                            | State | Notes                                                                                                                                                                 |
| ------------------------------------- | ----- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Detection (canonical UPX)             | Yes   | Section names UPX0/UPX1/UPX2; `UPX!` packer-header magic; `$Info: This file is packed with the UPX…` tooling banner                                                   |
| Detection (tampered)                  | Yes   | Brute-force PackHeader scan validates every field even with magic wiped. Structural fingerprint requires BSS-style first section (`RawSize=0` + `VirtualSize>0`)      |
| NRV2B_LE32 (method 2)                 | Yes   | In-process via `BB_Nrv2b`                                                                                                                                             |
| NRV2D_LE32 (method 3)                 | Yes   | In-process via `BB_Nrv2d` (UCL-spec port)                                                                                                                             |
| NRV2E_LE32 (method 8)                 | Yes   | In-process via `BB_Nrv2e` (UCL-spec port)                                                                                                                             |
| NRV2B_LE16 / LE8 (methods 4, 6)       | Yes   | `Nrv2bBuildingBlock.DecompressRaw{Le16,Byte}`                                                                                                                         |
| NRV2D_LE16 / LE8 (methods 5, 7)       | Yes   | `Nrv2dBuildingBlock.DecompressRaw{Le16,Byte}`                                                                                                                         |
| NRV2E_LE16 / LE8 (methods 9, 10)      | Yes   | `Nrv2eBuildingBlock.DecompressRaw{Le16,Byte}`                                                                                                                         |
| LZMA (method 14)                      | Yes   | Via `BB_Lzma`                                                                                                                                                         |
| DEFLATE (method 15)                   | No    | Rare in UPX output; deferred                                                                                                                                          |
| PE header reconstruction              | No    | Surfaces `decompressed_payload.bin` as a raw blob; IAT reconstruction + OEP restoration is delegated to `upx -d`                                                      |

Detection confidence is exposed as a 3-tier `DetectionConfidence` enum:

- **None** — no evidence; descriptor's `List` / `Extract` throws so `FormatDetector` falls back to plain PE/ELF resource enumeration.
- **Heuristic** — structural fingerprint match (BSS-style first section + RWX flags + entry in last section + high entropy) but no PackHeader.
- **Confirmed** — PackHeader found (with or without intact magic), canonical section names present, or tooling banner intact.

The evidence record exposes every contributing signal so users can audit *why* a binary was flagged.

#### Demoscene and historical packers (detection-only)

Full decompression would require the original tool's runtime stub or a bespoke decompressor that has not been ported.

| Packer                                                                   | Container           | Signature                                                                       | Reference                                                                                           |
| ------------------------------------------------------------------------ | ------------------- | ------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------- |
| [PKLITE](https://en.wikipedia.org/wiki/PKLITE)                           | DOS `.exe`          | `PKLITE Copr.` / `PKlite Copr.` in first 1 KB                                   | [bp/pklite](http://bp.dynsite.net/pklite.html)                                                      |
| [LZEXE](https://en.wikipedia.org/wiki/LZEXE)                             | DOS `.exe`          | `LZ91` / `LZ09` signature in first 1 KB                                         | [Bellard's page](https://bellard.org/)                                                              |
| Petite                                                                   | Win32 PE            | `.petite*` section name or `Petite` literal                                     | [Un4seen Petite](http://www.un4seen.com/petite/)                                                   |
| [Shrinkler](https://www.pouet.net/prod.php?which=64198)                  | Amiga HUNK          | HUNK magic (`0x000003F3`) + `Shrinkler` literal                                 | [Blueberry's repo](https://github.com/askeksa/Shrinkler)                                            |
| FSG                                                                      | Win32 PE            | `FSG!` magic in first 16 KB                                                     | [x86asm forum](http://x86asm.net/)                                                                 |
| MEW                                                                      | Win32 PE            | Section name starting with `.MEW` / `MEW`                                       | [Northfox page](http://www.nothings.org/)                                                          |
| MPRESS                                                                   | Win32 PE / Linux ELF| `.MPRESS1` / `.MPRESS2` section or `MPRESS` / `MATCODE` literal                 | [MATCODE](http://www.matcode.com/mpress.htm)                                                       |
| [Crinkler](https://en.wikipedia.org/wiki/Crinkler)                       | Win32 PE            | `Crinkler` / `crinkler` literal                                                 | [crinkler.net](http://www.crinkler.net/)                                                           |
| kkrunchy                                                                 | Win32 PE            | `kkrunchy` literal                                                              | [Farbrausch](https://github.com/farbrausch/fr_public)                                              |
| [ASPack](https://en.wikipedia.org/wiki/ASPack)                           | Win32 PE            | `.aspack` / `.adata` section or `ASPack` literal                                | [aspack.com](https://www.aspack.com/)                                                              |
| NsPack                                                                   | Win32 PE            | `.nsp*` section name or `NsPack` literal                                        | [PEiD DB](https://www.aldeid.com/wiki/PEiD)                                                        |
| Yoda's Crypter                                                           | Win32 PE            | `.yC` / `yC` section or `Yoda's` literal                                        | [Yoda's site](http://yodap.sourceforge.net/)                                                       |
| [ASProtect](https://en.wikipedia.org/wiki/ASProtect)                     | Win32 PE            | `ASProtect` literal                                                             | [aspack.com/asprotect](https://www.aspack.com/asprotect.html)                                      |
| [Themida](https://en.wikipedia.org/wiki/Themida)                         | Win32 PE            | `Themida` / `WinLicense` literal                                                | [Oreans](https://www.oreans.com/themida.php)                                                       |
| [VMProtect](https://en.wikipedia.org/wiki/VMProtect)                     | Win32 PE            | `.vmp*` section or `VMProtect` literal                                          | [vmpsoft.com](https://vmpsoft.com/)                                                                |

#### UCL-family building blocks

| BB         | Description                                                              | Status                                  |
| ---------- | ------------------------------------------------------------------------ | --------------------------------------- |
| `BB_Nrv2b` | UCL NRV2B LE32 — LZ77 + interleaved variable-length integer bit stream   | Spec-faithful, UPX-compatible decoder   |
| `BB_Nrv2d` | UCL NRV2D LE32 — three-bit-per-iter offset varint + low-bit length tying | Spec-faithful, UPX-compatible decoder   |
| `BB_Nrv2e` | UCL NRV2E LE32 — entropy-refined NRV2D variant                           | Spec-faithful, UPX-compatible decoder   |
| `BB_Lzma`  | LZMA dictionary compressor                                               | Pre-existing                            |

Each UCL BB emits a 4-byte little-endian uncompressed-size header so the building block can round-trip standalone via `IBuildingBlock.Compress` / `Decompress`. `Nrv2{b,d,e}BuildingBlock.DecompressRaw(compressed, exactOutputSize)` helpers are available for callers reading bare streams (UPX payloads, OS/2 drivers, retro-computing collections) without the size header.

---

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
| `info <archive>`               | -       | Show detailed archive information                           |
| `convert <input> <output>`     | -       | Convert between archive formats                             |
| `optimize <input> <output>`    | `opt`   | Re-encode with optimal compression                          |
| `benchmark <file>`             | `bench` | Benchmark all building blocks on the supplied data       |
| `analyze <file>`               | -       | Run binary analysis (detection + entropy + trial decompress)|
| `auto-extract <file>`          | -       | Recursive nested extraction (see below)                     |
| `batch <dir>`                  | -       | Scan a directory in parallel and aggregate format stats     |
| `suggest <file>`               | -       | Platform-aware format recommendation                        |
| `recover <image>`              | -       | Forensic carving — finds embedded filesystems + files in damaged disk images. `--mode auto\|filesystems\|files`, `--recursive` walks nested wrappers (e.g. ZIP→VHD→MBR→FAT). |
| `visualize <file>`             | -       | Renders a colored block map of every detected envelope (FAT/ext/NTFS/MBR/...) stacked by depth. `--format ascii\|svg\|html` |
| `carve <file>`                 | -       | Photorec-style file carver (JPEG/PNG/MP4/ZIP/... at any offset, including in slack space) |
| `reverse-engineer <tool>`      | `reveng`| Black-box probing of an unknown compression tool            |
| `tool (init\|list\|add\|run\|remove)` | - | Manage external-tool templates                          |
| `formats`                      | -       | List all supported formats                                  |

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

**Black-box tool probing** runs the target tool with ~40 controlled probe inputs (empty, single byte, incrementing patterns, text, random data, various sizes 0–64 KB), cross-correlates all outputs, and reports: magic bytes, size-field offsets (LE/BE, 2/4/8 byte), the compression algorithm (trial decompression against all 49 building blocks), filename storage (UTF-8 / UTF-16), determinism, payload entropy.

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
