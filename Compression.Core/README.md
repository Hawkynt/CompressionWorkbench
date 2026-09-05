# Hawkynt.Compression.Core

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.Compression.Core.svg)](https://www.nuget.org/packages/Hawkynt.Compression.Core/)
[![NuGet downloads](https://img.shields.io/nuget/dt/Hawkynt.Compression.Core.svg)](https://www.nuget.org/packages/Hawkynt.Compression.Core/)
[![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
![Target](https://img.shields.io/badge/target-net10.0-blue)

> Pure-managed compression primitives, entropy coders, transforms, hashing, bit I/O, and reusable building blocks implemented clean-room in C# with no native compression dependency.

## 📦 Installation

```bash
dotnet add package Hawkynt.Compression.Core
```

The package also bundles `Compression.Registry`, so the common `IBuildingBlock` registry surface is available from the same NuGet installation.

## ✨ Features

- Clean-room implementations based on public specifications and papers rather than ports of native compression libraries.
- Composable dictionary coders, entropy coders, transforms, filters, integer codes, hashes, and bit-level I/O.
- Uniform registry surface for comparing and composing building blocks.
- Direct concrete APIs remain available when an algorithm exposes meaningful non-default parameters.
- Pure managed code for environments where native `zlib`, `liblzma`, `libarchive`, or similar dependencies are undesirable.
- Round-trip-oriented test coverage plus official/reference vectors where available.

## 🧩 Support matrix

This is a curated package-level map, not a hand-maintained claim to list every compiled building block. For the exact inventory in the version you reference, query `BuildingBlockRegistry.All`.

| Algorithm / primitive | Family | State | Notes | Reference |
| --- | --- | :---: | --- | --- |
| [DEFLATE](https://en.wikipedia.org/wiki/Deflate) | Dictionary + entropy | R/W | Raw RFC 1951 building block | [RFC 1951](https://www.rfc-editor.org/rfc/rfc1951) |
| [LZ77](https://en.wikipedia.org/wiki/LZ77_and_LZ78) | Dictionary | R/W | Sliding-window dictionary coding | [Ziv & Lempel 1977](https://ieeexplore.ieee.org/document/1055714) |
| [LZ78](https://en.wikipedia.org/wiki/LZ77_and_LZ78) | Dictionary | R/W | Phrase-dictionary coding | [Ziv & Lempel 1978](https://ieeexplore.ieee.org/document/1055934) |
| [LZW](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Welch) | Dictionary | R/W | Variable-width dictionary coding | [Welch 1984](https://ieeexplore.ieee.org/document/1659158) |
| [LZ4](https://en.wikipedia.org/wiki/LZ4_(compression_algorithm)) | Dictionary | R/W | Fast block compression | [LZ4 block format](https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md) |
| [Snappy](https://en.wikipedia.org/wiki/Snappy_(compression)) | Dictionary | R/W | Fast block compression | [Snappy format](https://github.com/google/snappy/blob/main/format_description.txt) |
| [Brotli](https://en.wikipedia.org/wiki/Brotli) | Dictionary + entropy | R/W ⚠️ | Decoder accepts more of the format than the encoder chooses to emit | [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932) |
| [LZMA](https://en.wikipedia.org/wiki/Lempel%E2%80%93Ziv%E2%80%93Markov_chain_algorithm) | Dictionary + range coding | R/W | LZMA primitive | [7-Zip LZMA SDK](https://www.7-zip.org/sdk.html) |
| [LZX](https://en.wikipedia.org/wiki/LZX) | Dictionary + Huffman | R/W | Used by CAB/CHM/WIM families | [Microsoft LZX](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-patch/) |
| [Zstandard entropy stages](https://en.wikipedia.org/wiki/Zstd) | FSE / Huffman | R/W | Reusable entropy components | [RFC 8878](https://www.rfc-editor.org/rfc/rfc8878) |
| [Huffman coding](https://en.wikipedia.org/wiki/Huffman_coding) | Entropy | R/W | Static/canonical Huffman primitives | [Huffman 1952](https://ieeexplore.ieee.org/document/4051119) |
| [Arithmetic coding](https://en.wikipedia.org/wiki/Arithmetic_coding) | Entropy | R/W | Adaptive arithmetic coder | [Witten, Neal & Cleary 1987](https://dl.acm.org/doi/10.1145/214762.214771) |
| [Range coding](https://en.wikipedia.org/wiki/Range_coding) | Entropy | R/W | Arithmetic-coding family primitive | [Martin 1979](https://www.compressconsult.com/rangecoder/) |
| [rANS](https://en.wikipedia.org/wiki/Asymmetric_numeral_systems) | Entropy | R/W | Range Asymmetric Numeral Systems | [Duda 2009](https://arxiv.org/abs/0902.0271) |
| [FSE](https://en.wikipedia.org/wiki/Asymmetric_numeral_systems#Tabled_variant_(tANS)) | Entropy | R/W | Finite State Entropy / tANS | [FSE project](https://github.com/Cyan4973/FiniteStateEntropy) |
| [PPM](https://en.wikipedia.org/wiki/Prediction_by_partial_matching) | Context modelling | R/W | Prediction by Partial Matching | [Cleary & Witten 1984](https://ieeexplore.ieee.org/document/1096090) |
| [Context Tree Weighting](https://en.wikipedia.org/wiki/Context_tree_weighting) | Context modelling | R/W | Universal context weighting | [Willems et al.](https://ieeexplore.ieee.org/document/382012) |
| [Burrows-Wheeler transform](https://en.wikipedia.org/wiki/Burrows%E2%80%93Wheeler_transform) | Transform | R/W | Reversible block transform | [Burrows & Wheeler 1994](https://www.hpl.hp.com/techreports/Compaq-DEC/SRC-RR-124.pdf) |
| [Move-to-front](https://en.wikipedia.org/wiki/Move-to-front_transform) | Transform | R/W | Often paired with BWT | [Bentley et al. 1986](https://dl.acm.org/doi/10.1145/6424.6429) |
| [Run-length encoding](https://en.wikipedia.org/wiki/Run-length_encoding) | Transform | R/W | Generic RLE stages | [Overview](https://en.wikipedia.org/wiki/Run-length_encoding) |
| [CRC-32C](https://en.wikipedia.org/wiki/Cyclic_redundancy_check) | Hash/checksum | Compute | Hardware-assisted where available | [RFC 3720 Appendix B](https://www.rfc-editor.org/rfc/rfc3720#appendix-B) |
| [xxHash](https://en.wikipedia.org/wiki/xxHash) | Hash | Compute | Fast non-cryptographic hashing | [xxHash](https://xxhash.com/) |
| [BLAKE2](https://en.wikipedia.org/wiki/BLAKE_(hash_function)#BLAKE2) | Hash | Compute | Cryptographic hash family | [RFC 7693](https://www.rfc-editor.org/rfc/rfc7693) |

`R/W` means the primitive exposes both compression/encoding and decompression/decoding paths. `⚠️` marks a deliberate subset whose limits matter for interoperability.

## 🚀 Quick start

### Registry-based round trip

```csharp
using Compression.Registry;

IBuildingBlock lzw = BuildingBlockRegistry.GetById("BB_Lzw")!;
byte[] compressed = lzw.Compress(originalBytes);
byte[] restored = lzw.Decompress(compressed);
```

### Concrete LZW parameters

```csharp
using Compression.Core.Dictionary.Lzw;

using var stream = new MemoryStream();
var encoder = new LzwEncoder(stream, minBits: 9, maxBits: 12);
encoder.Encode(originalBytes);

stream.Position = 0;
var decoder = new LzwDecoder(stream, minBits: 9, maxBits: 12);
byte[] restored = decoder.Decode(originalBytes.Length);
```

### Bit I/O

```csharp
using Compression.Core.BitIO;

using var stream = new MemoryStream();
var writer = new BitWriter(stream, BitOrder.MsbFirst);
writer.WriteBits(0b1011_0010, count: 8);
writer.WriteBits(0xF, count: 4);
writer.Flush();

stream.Position = 0;
var reader = new BitReader(stream, BitOrder.MsbFirst);
int byteValue = reader.ReadBits(8);
int nibble = reader.ReadBits(4);
```

### Hashing

```csharp
using Compression.Core.Hashing;

uint crc = Crc32C.Compute(data);
uint xx32 = XxHash32.Compute(data);
ulong fnv = Fnv1a64.Compute(data);
byte[] sha256 = Sha256.Compute(data);
```

## 📚 Choosing a building block

| Goal | Typical family to inspect |
| --- | --- |
| Fast dictionary compression | LZ4 / Snappy / LZO-style implementations present in the registry |
| General-purpose dictionary + entropy compression | DEFLATE / LZMA / Brotli-related implementations present in the registry |
| Transform pipelines | BWT → MTF → entropy coding |
| Adaptive entropy coding | Arithmetic / range / ANS-family implementations |
| Integer coding | Golomb/Rice, Exp-Golomb, Elias-family implementations |
| Research/experimental comparison | Whatever the current registry exposes for that build |

The source-repository CLI can benchmark the currently registered building blocks on representative input:

```bash
+cwb benchmark sample.bin
```

`BuildingBlockRegistry.All` is the authoritative list of what the referenced build actually contains.

## 🧭 Package boundary

`Compression.Core/Hawkynt.Compression.Core.csproj` is packable and uses the project filename as the NuGet package ID: `Hawkynt.Compression.Core`.

The project references `Compression.Registry` with `PrivateAssets="all"` and adds the resolved registry assembly to the package's `lib/<tfm>` output. Consumers therefore install one NuGet package while still getting the registry contracts used by Core.

The repository currently contains these other packable public package projects alongside Core:

| Package | Project | Purpose |
| --- | --- | --- |
| `Hawkynt.FileFormats.Audio` | `Hawkynt.FileFormats.Audio/Hawkynt.FileFormats.Audio.csproj` | Audio codecs and audio/container formats |
| `Hawkynt.FileFormats.Archives` | `Hawkynt.FileFormats.Archives/Hawkynt.FileFormats.Archives.csproj` | Compression streams and archive/container formats |
| `Hawkynt.FileFormats.FileSystems` | `Hawkynt.FileFormats.FileSystems/Hawkynt.FileFormats.FileSystems.csproj` | Filesystems and disk-image formats |

No additional package is named here unless a corresponding checked-in package surface actually exists.

## 🏗️ Implementation structure

Core is the reusable primitive layer. Its checked-in code is organised around concerns such as:

| Area | Examples | Role |
| --- | --- | --- |
| Bit I/O | `BitReader`, `BitWriter`, `BitOrder` | Bit-level parsing and emission used by variable-length coders |
| Dictionary coding | LZ-family implementations, DEFLATE-related primitives | Match-based compression building blocks |
| Entropy coding | Huffman, arithmetic/range coding, ANS-family primitives | Symbol coding and probability-model stages |
| Transforms | BWT, MTF, RLE, delta/BCJ-style transforms | Reversible preprocessing stages |
| Hashing/checksums | CRC-family, xxHash-family and cryptographic hashes present in Core | Integrity, lookup and format support |
| SIMD helpers | match-length/copy/histogram helpers | Accelerated hot-path primitives where supported |
| Streams | sub/concatenated stream helpers | Reusable format-reader plumbing |
| Disk-image helpers | MBR/GPT and partition-related primitives | Shared lower-level disk/container parsing |

The registry provides a common comparison surface; it does not erase algorithm-specific semantics. Concrete APIs remain appropriate when streaming, allocation, tuning parameters, or format-specific behavior matters.

A primitive used by a file format does not by itself imply full support for that format, and decode and encode coverage may legitimately be asymmetric. Documentation keeps those claims separate.

## 🔬 Selected implementation caveats

The public support matrix marks deliberate subsets with `⚠️`. Brotli is one example: the decoder accepts more of the format than the encoder chooses to emit. Such distinctions belong in the support table and implementation discussion rather than being hidden behind a generic “supported” label.

For algorithm-specific investigations, inspect the implementation, nearby comments/tests, and dedicated repository documents where they exist. Examples include `docs/LZMS-ON-DISK.md` and `Compression.Core/SqxFormat/README.md`.

## 🧪 Verification

The repository test suite is the evidence source for implementation claims. Relevant test styles include:

- compress → decompress byte-identical round trips;
- official or specification-derived vectors where available;
- external-tool interoperability tests where the repository provides them;
- targeted regression tests for discovered edge cases.

An intended feature, TODO, experiment, issue, or roadmap item is not a support claim until the implementation and corresponding evidence exist.

## 🔖 Versioning

The checked-in base version comes from the nearest MSBuild version declaration. At repository level, `Directory.Build.props` currently declares:

```xml
<Version>1.0.0</Version>
```

The repository's `.github/workflows/scripts/version.pl` composes .NET package versions as `X.Y.Z.BUILD`, where `BUILD` is derived from the commit count for the directory that declares the effective version. Release workflows may pass that computed version into packing.

A consumer should reference the actual NuGet package ID:

```xml
<PackageReference Include="Hawkynt.Compression.Core" Version="1.0.0" />
```

Use the concrete version you intend to consume; this document does not predict a future release number or stability milestone.

## 📚 API reference

<!-- API:BEGIN generated by Hawkynt/RepositoryTemplate/package-readme — edit the XML docs in source, not here -->

Every public and protected member of all 602 types, generated from the built assembly and its XML documentation, is in [REFERENCE.md](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Compression.Core/REFERENCE.md).

<!-- API:END -->

## 🔌 Dependencies

| Dependency | Packaging behaviour |
| --- | --- |
| `Compression.Registry` | Project dependency bundled into Core's package output |
| Native compression libraries | None required by the Core package design |

Audio, archive and filesystem functionality lives in the separate package projects listed above and is not pulled into Core transitively.

## ⚠️ Limitations

- A common API shape does not imply identical streaming, memory, or parameter semantics across every algorithm; use concrete APIs when those distinctions matter.
- Some encoders intentionally implement a standards-compliant subset while decoders accept a wider format.
- Pure managed code is the design goal, not an automatic speed claim. BCL/native implementations may be faster for common algorithms on some workloads.
- Do not infer a package, format, algorithm, profile, or release state from roadmap intent. Checked-in project files, the compiled registry/public API, and tests are the evidence sources.
- Do not state volatile algorithm counts unless they are generated from the registry/build.
- When code and prose disagree, the compiled registry/API and tests win; update the prose.

## 🤝 Contributing

Open issues and pull requests in [Hawkynt/CompressionWorkbench](https://github.com/Hawkynt/CompressionWorkbench). Repository contribution and CI rules are documented in `AGENTS.md` and `CONTRIBUTING.md`.

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
