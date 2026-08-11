# Hawkynt.Compression.Core

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.Compression.Core.svg)](https://www.nuget.org/packages/Hawkynt.Compression.Core/)
[![License](https://img.shields.io/badge/license-LGPL--3.0--or--later-blue)](https://www.gnu.org/licenses/lgpl-3.0.html)

> Pure-managed compression primitives extracted from
[CompressionWorkbench](https://github.com/Hawkynt/CompressionWorkbench).
Every algorithm is implemented from scratch — no native dependency on zlib, liblzma, libarchive, or any
other third-party compression library. The package is single-purpose: it bundles only
`Compression.Registry` (the building-block + format-descriptor interfaces) directly into `lib/`,
so consumers add a single dependency and get the full primitive surface. PCM audio, archive
formats, and filesystem readers ship as separate single-responsibility packages — alongside
the sibling `Hawkynt.FileFormats.Images` — and are not pulled in transitively by
`Hawkynt.Compression.Core`:

- `Hawkynt.FileFormats.Audio` — PCM / FLAC / WAV / AIFF / AU / Vorbis / Opus / AAC / MP3
- `Hawkynt.FileFormats.Archives` — ZIP / TAR / 7Z / RAR / and the long tail
- `Hawkynt.FileFormats.FileSystems` — FAT / ext4 / NTFS / HFS+ / APFS / Btrfs / etc.

## When to use this package

Reach for `Compression.Core` when **any** of these is true:

- You need a compression algorithm that's not in the BCL (LZW / LZMA / LZHAM / Brotli / Zopfli / FSE /
  rANS / PAQ8 / context-mixing / Burrows-Wheeler / arithmetic / range / Tunstall / DMC / etc.)
- You need a fully managed implementation (e.g. for AOT, deterministic-build, or environments where
  binding against `libzlib` is forbidden by security review)
- You need fine-grained control: bit-level I/O, raw entropy stages, transform composition, custom match
  finders — building blocks compose freely
- You're benchmarking algorithms against each other and want a level playing field — every codec here
  uses the same `byte[] Compress(ReadOnlySpan<byte>)` / `byte[] Decompress(ReadOnlySpan<byte>)` shape

Skip it when:

- You only need DEFLATE / GZIP / Brotli / ZLib at default settings — `System.IO.Compression` is faster
  on hot paths because it ships with native code paths
- You need format-level handling (ZIP / TAR / 7Z / RAR archives) — wait for
  `Hawkynt.CompressionWorkbench.Lib` (sister package, not yet on NuGet) or use the source repo

---

## Quick start

### Algorithm round-trip

Every building block exposes the same two-method shape via the
`Compression.Registry.IBuildingBlock` interface:

```csharp
using Compression.Core.Dictionary.Lzw;
using Compression.Registry;   // IBuildingBlock + BuildingBlockRegistry

IBuildingBlock lzw = BuildingBlockRegistry.GetById("BB_Lzw")!;
byte[] compressed = lzw.Compress(originalBytes);
byte[] roundTripped = lzw.Decompress(compressed);
```

Or call the concrete codec directly when you want non-default parameters:

```csharp
using var ms = new MemoryStream();
var encoder = new LzwEncoder(ms, minBits: 9, maxBits: 12);
encoder.Encode(originalBytes);

ms.Position = 0;
var decoder = new LzwDecoder(ms, minBits: 9, maxBits: 12);
byte[] roundTripped = decoder.Decode(originalBytes.Length);
```

### Hashing

```csharp
using Compression.Core.Hashing;

uint crc      = Crc32C.Compute(data);  // hardware-accelerated SSE4.2 / ARM CRC32C
uint xx32     = XxHash32.Compute(data);
ulong fnv     = Fnv1a64.Compute(data);
byte[] sha256 = Sha256.Compute(data);
```

### Bit I/O

```csharp
using Compression.Core.BitIO;

using var ms = new MemoryStream();
var writer = new BitWriter(ms, BitOrder.MsbFirst);
writer.WriteBits(0b1011_0010, count: 8);
writer.WriteBits(0xF,         count: 4);
writer.Flush();

ms.Position = 0;
var reader = new BitReader(ms, BitOrder.MsbFirst);
int byteVal = reader.ReadBits(8);   // 0xB2
int nibble  = reader.ReadBits(4);   // 0xF
```

### Discovering building blocks

```csharp
foreach (var bb in BuildingBlockRegistry.All)
  Console.WriteLine($"{bb.Id,-18} {bb.Family,-14} {bb.Description}");
```

The benchmark tool in the source repo (`cwb benchmark <file>`) iterates this same list and ranks every
algorithm by ratio + compress / decompress wall time on a single input.

---

## Choosing the right algorithm

| You want… | Pick |
|---|---|
| Fastest possible round-trip on logs / JSON / mixed data | `BB_Lz4`, `BB_Snappy`, `BB_Pithy`, `BB_Lzfx`, `BB_Lzo`, `BB_Lzjb` |
| Best ratio with reasonable CPU budget (general purpose) | `BB_Lzma`, `BB_Brotli`, `BB_Zstd-via-FSE` |
| Best ratio at any CPU cost (research / archival) | `BB_PAQ8`, `BB_Cmix`, `BB_Mcm`, `BB_Bsc`, `BB_Csc`, `BB_Zpaq` |
| Streamable byte-level codec (network / pipe) | `BB_Deflate`, `BB_Lz4`, `BB_Snappy` |
| Lossless image-friendly | `BB_Deflate` (PNG layer), `BB_Lzma` (lossless), `BB_Brotli` (modern web) |
| Domain transform first, then entropy code | `BB_Bwt` → `BB_Mtf` → `BB_Huffman` (the bzip2 stack) |
| Adaptive entropy stage | `BB_Arithmetic`, `BB_RangeCoding`, `BB_rANS`, `BB_FSE` |
| Static entropy stage (precomputed table) | `BB_Huffman`, `BB_Tunstall`, `BB_ShannonFano` |
| Geometric integer distributions | `BB_Golomb`, `BB_Rice`, `BB_ExpGolomb`, `BB_EliasGamma`, `BB_EliasDelta` |
| Universal integer codes (no model) | `BB_Unary`, `BB_Fibonacci`, `BB_Levenshtein` |
| DNA / 4-symbol alphabets | `BB_Dna` (specialised) |
| Hardware-friendly chunked compression | `BB_842` (IBM hardware-style 2 / 4 / 8-byte template matching) |
| Network-protocol-style framing (packet sizes) | `BB_Lzs` (Stac LZS — RFC 1967 / RFC 2395) |
| Templated grammar / repetition mining | `BB_RePair`, `BB_Sequitur`, `BB_Bpe`, `BB_Lzwl` |

When in doubt: run `cwb benchmark` from the source repo on a representative sample of your data and let
the numbers pick.

---

## Building blocks reference

Every entry below registers via `IBuildingBlock`, exposes both `Compress` and `Decompress`, and was
verified via the project's round-trip test matrix. Each row links to the algorithm's primary reference
or canonical specification.

| Id | Algorithm | Family | State | Description | Reference |
|---|---|---|---|---|---|
| `BB_Deflate` | [DEFLATE](https://en.wikipedia.org/wiki/Deflate) | Dictionary | R/W | LZ77 + Huffman, the algorithm inside gzip / zip / png | [RFC 1951](https://www.rfc-editor.org/rfc/rfc1951) |
| `BB_Deflate64` | [Deflate64](https://en.wikipedia.org/wiki/Deflate#Deflate64) | Dictionary | R/W | Enhanced DEFLATE with 64 KB window | [MS-ZIP](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-zip/) |
| `BB_Lz77` | [LZ77](https://en.wikipedia.org/wiki/LZ77_and_LZ78) | Dictionary | R/W | Sliding-window dictionary with distance / length tokens | [Ziv & Lempel 1977](https://www2.cs.duke.edu/courses/spring03/cps296.5/papers/ziv_lempel_1977_universal_algorithm.pdf) |
| `BB_Lz78` | [LZ78](https://en.wikipedia.org/wiki/LZ77_and_LZ78) | Dictionary | R/W | Phrase-dictionary precursor to LZW | [Ziv & Lempel 1978](https://ieeexplore.ieee.org/document/1055934) |
| `BB_Lzw` | [LZW](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Welch) | Dictionary | R/W | Dictionary coding used in GIF / Unix `compress` | [Welch 1984](https://www.cs.duke.edu/courses/spring03/cps296.5/papers/welch_1984_technique_for.pdf) |
| `BB_Lzo` | [LZO1X](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Oberhumer) | Dictionary | R/W | Extremely fast, decompression-optimised | [oberhumer.com](http://www.oberhumer.com/opensource/lzo/) |
| `BB_Lzss` | [LZSS](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Storer%E2%80%93Szymanski) | Dictionary | R/W | LZ77 variant with flag-bit encoding | [Storer & Szymanski 1982](https://dl.acm.org/doi/10.1145/322344.322346) |
| `BB_Lz4` | [LZ4](https://en.wikipedia.org/wiki/LZ4_(compression_algorithm)) | Dictionary | R/W | Extremely fast LZ77-family block compression | [LZ4 block format](https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md) |
| `BB_Snappy` | [Snappy](https://en.wikipedia.org/wiki/Snappy_(compression)) | Dictionary | R/W | Fast LZ77-family compression (Google) | [Snappy format](https://github.com/google/snappy/blob/main/format_description.txt) |
| `BB_Brotli` | [Brotli](https://en.wikipedia.org/wiki/Brotli) | Dictionary | R/W | LZ77 + Huffman with static dictionary (Google). The decoder reads the whole format; the encoder uses literal context modelling, the distance ring buffer, run-length coded prefix code descriptors and cost-driven meta-block splitting, but emits no static dictionary references, no block-switch commands and no non-zero NPOSTFIX/NDIRECT. | [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932) |
| `BB_Lzma` | [LZMA](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Markov_chain_algorithm) | Dictionary | R/W | LZ + Markov chain + range coding | [7-Zip LZMA SDK](https://www.7-zip.org/sdk.html) |
| `BB_Lzx` | [LZX](https://en.wikipedia.org/wiki/LZX) | Dictionary | R/W | LZ77 + Huffman used in CAB / CHM / WIM | [MS-PATCH LZX](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-patch/) |
| `BB_Xpress` | [XPRESS Huffman](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-xca/) | Dictionary | R/W | Windows XPRESS (NTFS / WIM / Hyper-V) | [MS-XCA](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-xca/) |
| `BB_Lzh` | [LZH (LH5)](https://en.wikipedia.org/wiki/LHA_(file_format)) | Dictionary | R/W ¹ | Lempel-Ziv with adaptive Huffman, used in LHA | [LZH format doc](http://web.archive.org/web/20021013020141/http://www.osirusoft.com/joejared/lzhformat.html) |
| `BB_Arj` | [ARJ](https://en.wikipedia.org/wiki/ARJ) | Dictionary | R/W ¹ | Modified LZ77 + Huffman from ARJ archives | [ARJ technote](http://www.arjsoftware.com/) |
| `BB_Lzms` | [LZMS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-xca/) | Dictionary | R/W | LZ + Markov + Shannon delta (Windows WIM / ESD) | [MS-XCA LZMS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-xca/) |
| `BB_Lzp` | [LZP](https://en.wikipedia.org/wiki/LZP) | Dictionary | R/W | Lempel-Ziv Prediction with context-based match prediction | [Bloom 1996](https://encode.su/threads/1301-LZP) |
| `BB_Ace` | [ACE](https://en.wikipedia.org/wiki/ACE_(compressed_file_format)) | Dictionary | R/W | LZ77 + Huffman from ACE archive format | [unace-nonfree](https://gitlab.com/luser_droog/unace-nonfree) |
| `BB_Rar` | [RAR5](https://en.wikipedia.org/wiki/RAR_(file_format)) | Dictionary | R/W | LZ + Huffman + PPM from RAR5 | [rarlab technote](https://www.rarlab.com/technote.htm) |
| `BB_Sqx` | SQX | Dictionary | R/W ¹ | LZ + Huffman from the SQX archive format | [SqxFormat notes](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Compression.Core/SqxFormat/README.md) |
| `BB_ROLZ` | [ROLZ](https://en.wikipedia.org/wiki/LZ77_and_LZ78#Variants) | Dictionary | R/W | Reduced-Offset LZ with context-based match tables | [encode.su](https://encode.su/threads/1909-ROLZ) |
| `BB_LZHAM` | [LZHAM](https://github.com/richgel999/lzham_codec) | Dictionary | R/W | LZ77 + Huffman, inspired by LZHAM codec | [LZHAM repo](https://github.com/richgel999/lzham_codec) |
| `BB_Lzs` | [LZS](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Stac) | Dictionary | R/W | Stac LZS (7 / 11-bit offset LZSS for networking) | [RFC 1967](https://www.rfc-editor.org/rfc/rfc1967) / [RFC 2395](https://www.rfc-editor.org/rfc/rfc2395) |
| `BB_Lzwl` | LZWL | Dictionary | R/W | LZW with variable-length initial alphabet from digram analysis | [LZWL paper](https://www.jucs.org/jucs_9_9/compression_with_finite_mixed/jucs_9_9_1055_1081_salomon.pdf) |
| `BB_RePair` | [Re-Pair](https://en.wikipedia.org/wiki/Re-Pair) | Dictionary | R/W | Recursive Pairing, offline grammar-based compression | [Larsson & Moffat 1999](https://ieeexplore.ieee.org/document/755679) |
| `BB_Sequitur` | [Sequitur](https://en.wikipedia.org/wiki/Sequitur_algorithm) | Dictionary | R/W | Online grammar inference by digram uniqueness and rule utility | [Nevill-Manning & Witten 1997](https://www.jair.org/index.php/jair/article/view/10151) |
| `BB_842` | [842](https://en.wikipedia.org/wiki/842_(compression_algorithm)) | Dictionary | R/W | IBM 842 hardware compression with 2 / 4 / 8-byte template matching | [Linux `crypto/842*`](https://github.com/torvalds/linux/tree/master/crypto) |
| `BB_PPM` | [PPM](https://en.wikipedia.org/wiki/Prediction_by_partial_matching) | Context-Mixing | R/W | Prediction by Partial Matching — order-3 contexts, escape method C, full exclusion, arithmetic coded | [Cleary & Witten 1984](https://ieeexplore.ieee.org/document/1096090) / [Moffat 1990](https://ieeexplore.ieee.org/document/61469) |
| `BB_CTW` | [CTW](https://en.wikipedia.org/wiki/Context_tree_weighting) | Context-Mixing | R/W | Context Tree Weighting — optimal universal compression | [Willems 1995](https://ieeexplore.ieee.org/document/382012) |
| `BB_Huffman` | [Huffman](https://en.wikipedia.org/wiki/Huffman_coding) | Entropy | R/W | Optimal prefix-free entropy coding | [Huffman 1952](https://ieeexplore.ieee.org/document/4051119) |
| `BB_Arithmetic` | [Arithmetic](https://en.wikipedia.org/wiki/Arithmetic_coding) | Entropy | R/W | Order-0 arithmetic coding with frequency table | [Witten / Neal / Cleary 1987](https://dl.acm.org/doi/10.1145/214762.214771) |
| `BB_ShannonFano` | [Shannon-Fano](https://en.wikipedia.org/wiki/Shannon%E2%80%93Fano_coding) | Entropy | R/W | Recursive frequency-splitting precursor to Huffman | Shannon 1948 |
| `BB_Golomb` | [Golomb / Rice](https://en.wikipedia.org/wiki/Golomb_coding) | Entropy | R/W | Optimal coding for geometric distributions | [Golomb 1966](https://ieeexplore.ieee.org/document/1053899) |
| `BB_Fibonacci` | [Fibonacci](https://en.wikipedia.org/wiki/Fibonacci_coding) | Entropy | R/W | Universal code with `11` terminators (Zeckendorf) | [Apostolico & Fraenkel 1987](https://ieeexplore.ieee.org/document/1057294) |
| `BB_FSE` | [FSE / tANS](https://en.wikipedia.org/wiki/Asymmetric_numeral_systems) | Entropy | R/W | Table-based ANS (Zstd's entropy stage) | [Duda 2013](https://arxiv.org/abs/1311.2540) / [Collet's blog](https://fastcompression.blogspot.com/) |
| `BB_BPE` | [Byte Pair Encoding](https://en.wikipedia.org/wiki/Byte_pair_encoding) | Entropy | R/W | Iterative most-frequent-pair replacement | [Gage 1994](http://www.pennelynn.com/Documents/CUJ/HTML/94HTML/19940045.HTM) |
| `BB_RangeCoding` | [Range coding](https://en.wikipedia.org/wiki/Range_coding) | Entropy | R/W | Byte-oriented arithmetic coding with carryless normalisation | [Martin 1979](https://en.wikipedia.org/wiki/Range_coding#References) |
| `BB_rANS` | [rANS](https://en.wikipedia.org/wiki/Asymmetric_numeral_systems#Range_variants_(rANS)_and_streaming) | Entropy | R/W | Range ANS, used in AV1 and LZFSE | [Duda 2013](https://arxiv.org/abs/1311.2540) |
| `BB_ExpGolomb` | [Exp-Golomb](https://en.wikipedia.org/wiki/Exponential-Golomb_coding) | Entropy | R/W | Exponential Golomb, used in H.264 / H.265 | [Teuhola 1978](https://dl.acm.org/doi/10.1016/0020-0190(78)90043-2) |
| `BB_Unary` | [Unary](https://en.wikipedia.org/wiki/Unary_coding) | Entropy | R/W | Simplest universal code: N ones followed by a zero | — |
| `BB_EliasGamma` | [Elias gamma](https://en.wikipedia.org/wiki/Elias_gamma_coding) | Entropy | R/W | Universal code using a unary length prefix | [Elias 1975](https://ieeexplore.ieee.org/document/1055349) |
| `BB_EliasDelta` | [Elias delta](https://en.wikipedia.org/wiki/Elias_delta_coding) | Entropy | R/W | Gamma-codes the bit length | [Elias 1975](https://ieeexplore.ieee.org/document/1055349) |
| `BB_Levenshtein` | [Levenshtein](https://en.wikipedia.org/wiki/Levenshtein_coding) | Entropy | R/W | Self-delimiting universal code with recursive length prefixing | Levenshtein 1968 |
| `BB_Tunstall` | [Tunstall](https://en.wikipedia.org/wiki/Tunstall_coding) | Entropy | R/W | Variable-to-fixed code, dual of Huffman | Tunstall 1967 (PhD thesis) |
| `BB_Dmc` | [DMC](https://en.wikipedia.org/wiki/Dynamic_Markov_compression) | Entropy | R/W | Dynamic Markov Compression, bit-level FSM with state cloning | [Cormack & Horspool 1987](https://dl.acm.org/doi/10.1145/22899.22901) |
| `BB_Bwt` | [BWT](https://en.wikipedia.org/wiki/Burrows%E2%80%93Wheeler_transform) | Transform | R/W | Burrows-Wheeler Transform, reorders bytes for better compression | [Burrows & Wheeler 1994](https://www.hpl.hp.com/techreports/Compaq-DEC/SRC-RR-124.pdf) |
| `BB_Mtf` | [MTF](https://en.wikipedia.org/wiki/Move-to-front_transform) | Transform | R/W | Move-to-Front Transform | [Bentley et al. 1986](https://dl.acm.org/doi/10.1145/6138.6151) |
| `BB_Delta` | [Delta](https://en.wikipedia.org/wiki/Delta_encoding) | Transform | R/W | Delta filter, stores differences between consecutive bytes | — |
| `BB_DeltaRle` | [Delta](https://en.wikipedia.org/wiki/Delta_encoding) | Transform | R/W | Delta filter followed by run-length encoding of the delta stream; unlike `BB_Delta`, this compresses repetitive data | — |
| `BB_Rle` | [RLE](https://en.wikipedia.org/wiki/Run-length_encoding) | Transform | R/W | Run-Length Encoding | — |
| `BB_Dpcm` | [DPCM](https://en.wikipedia.org/wiki/Differential_pulse-code_modulation) | Transform | R/W | Differential PCM, sample-to-sample differences | — |

¹ Known edge cases on certain pathological inputs — see the source repo's known-fixes log. The mainline
round-trip works for all real-world data we've tested.

---

## Module overview

| Module | Surface | What's in it |
|---|---|---|
| `Compression.Core.BitIO` | `BitReader`, `BitWriter`, `BitBuffer`, `BitOrder` | LSB / MSB-first bit streams over `Stream`. Foundation for every entropy / variable-length coder in the package. |
| `Compression.Core.Hashing` | `Crc32`, `Crc32C`, `Adler32`, `XxHash32`, `Fletcher16/32/64`, `MurmurHash3`, `Fnv1` / `Fnv1a`, `Sha1`, `Sha256`, `Md5`, `Blake2b` | Non-cryptographic checksums and a few crypto hashes. CRC-32C uses SSE4.2 / ARM CRC32C instructions where available, with a slicing-by-4 software fallback. |
| `Compression.Core.Crypto` | `AesCbc`, `AesCtr`, `Blowfish`, `Pbkdf2`, `ZipCrypto` | Format-required ciphers (ZipCrypto for legacy ZIP, AES-256 for ZIP-AE). |
| `Compression.Core.DataStructures` | `SlidingWindow`, `PriorityQueue`, `Trie`, `SuffixTree` | Generic structures used by match finders and grammar coders. |
| `Compression.Core.Deflate` | `DeflateEncoder`, `DeflateDecoder`, `Zopfli`, `MsZip` | DEFLATE pipeline plus Zopfli's iterative ratio-optimisation pass and Microsoft's MS-ZIP variant. |
| `Compression.Core.Dictionary.*` | One namespace per dictionary algorithm | LZ77 / LZ78 / LZW / LZSS / LZ4 / Snappy / Brotli / LZMA / LZX / XPRESS / LZH / ARJ / LZMS / LZP / ACE / RAR5 / SQX / LZO1X / LZHAM / LZS / LZWL / Re-Pair / 842, plus building-block variants (LZJB / LZF / LZRW1 / LZRW3 / FastLZ / Lizard / QuickLZ / Pithy / Crush / LZG / LZRLE / LZMAT / Shoco / DnaCompression). |
| `Compression.Core.Entropy.*` | One namespace per entropy coder | Huffman (canonical) / Shannon-Fano / Arithmetic / Range / FSE / rANS / PPMd / context-mixing (PAQ8 / cmix / MCM / BCM / BSC / BALZ / CSC / Zling / Lizard / QuickLZ-as-entropy) / Golomb / Rice / Elias Gamma / Elias Delta / Fibonacci / Tunstall / Exp-Golomb / Unary / Levenshtein / DMC. |
| `Compression.Core.Transforms` | `Bwt`, `Mtf`, `Rle`, `Delta`, `Bcj`, `Bcj2`, `Bpe`, `Dpcm`, `RePair` | Reversible byte-rearrangement passes — staged before entropy coding. The BCJ family is platform-specific branch-relative-to-absolute conversion at full liblzma parity (x86 / ARM / Thumb / ARM64 / PPC / SPARC / IA64 / RISC-V), each exposed as a standalone building block; ARM64 is validated byte-for-byte against xz. |
| `Compression.Core.Simd` | `SimdMatchLength`, `SimdMemCopy`, `SimdHistogram`, `SimdRunScan` | Vector-accelerated primitives for hot match-finding paths. |
| `Compression.Core.Streams` | `SubStream`, `ConcatStream`, etc. | Lightweight stream shims commonly needed when writing format readers. |
| `Compression.Core.DiskImage` | `MbrParser`, `GptParser`, `PartitionTypeDatabase`, `PartitionEntry` | Header-only parsers for partition tables — handy when dealing with disk-image archives. |
| `Compression.Core.Progress` | `IProgress<>`, `Progress` helpers | Cancellable progress reporting for long compressors. |

---

## Versioning

Versions follow `MAJOR.MINOR.PATCH.BUILD` where `BUILD` is `git rev-list --count HEAD` of the source
repo, auto-stamped at release time by `scripts/version.pl`. The package is **pre-1.0** — public
surface may rearrange between releases. Once we ship 1.0.0, breaking changes follow SemVer
(`MAJOR` bump).

If you need a pinned reference, lock the exact 4-segment version:

```xml
<PackageReference Include="Compression.Core" Version="[1.0.0.123]" />
```

---

## Source, tests, and benchmarks

Lives in [Hawkynt/CompressionWorkbench](https://github.com/Hawkynt/CompressionWorkbench). The source
repo carries:

- A NUnit test suite (~10 000 cases including round-trip sweeps for every building block, the
  `cwb benchmark` matrix, and an external-tool interop suite that round-trips through `gzip` / `7z` /
  `xz` etc.)
- A WPF UI (`Compression.UI`) with a benchmark visualiser and an archive browser
- A CLI (`cwb`) with `compress` / `extract` / `benchmark` / `analyse` / `auto-extract` / `suggest`
  subcommands
- ~215 archive / file-system / image-format readers built on top of these primitives

## Contributing

Open an issue or PR on the source repo. The CI runs the full test matrix on Windows and Linux and
gates merges on a green test run.

## License

LGPL-3.0-or-later. See [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
in the source repo.
