# Architecture

This document describes the high-level architecture of CompressionWorkbench: how the projects relate to each other, how format detection works, how to extend the system, and the performance strategies used throughout.

---

## Project Structure

```
CompressionWorkbench.slnx
|
+-- Core
|   +-- Compression.Core                  Primitives and building blocks (Huffman, LZ77, BWT, etc.)
|   +-- Compression.Registry              Interfaces and registries (IFormatDescriptor, IBuildingBlock)
|   +-- Compression.Registry.Generator    Roslyn source generator for zero-reflection discovery
|   +-- Compression.Lib                   Umbrella library: format detection, archive ops, SFX builder
|
+-- FileFormats (180+ projects)
|   +-- FileFormat.Zip                    ZIP archive
|   +-- FileFormat.Gzip                   Gzip stream
|   +-- FileFormat.Rar                    RAR archive
|   +-- ...                               One project per file format
|
+-- Analysis
|   +-- Compression.Analysis              Binary analysis engine (signatures, entropy, fingerprinting,
|                                         reverse engineering, external tool integration, visualization)
|
+-- User Interfaces
|   +-- Compression.CLI                   Command-line tool (cwb)
|   +-- Compression.UI                    WPF archive browser and analysis wizard
|   +-- Compression.Shell                 Windows Explorer context menu integration
|
+-- Self-Extracting Archives
|   +-- Compression.Sfx.Cli              Console SFX stub
|   +-- Compression.Sfx.Ui               GUI SFX stub
|
+-- Tests
    +-- Compression.Tests                 NUnit test project (all tests in one project)
```

### Dependency Graph

```
FileFormat.* ---> Compression.Core     (algorithm primitives)
FileFormat.* ---> Compression.Registry (IFormatDescriptor, IStreamFormatOperations, IArchiveFormatOperations)

Compression.Lib -----> FileFormat.*              (ProjectReference for each format)
Compression.Lib -----> Compression.Core
Compression.Lib -----> Compression.Registry
Compression.Lib <----- Compression.Registry.Generator  (source generator, compile-time only)

Compression.Analysis -> Compression.Lib          (format detection, trial decompression)
Compression.CLI ------> Compression.Analysis
Compression.UI -------> Compression.Analysis
Compression.Tests ----> Compression.Lib, Compression.Analysis
```

Each `FileFormat.*` project is a small, self-contained library that implements one file format. It references only `Compression.Core` (for primitives) and `Compression.Registry` (for interfaces). It does not reference `Compression.Lib` or any other format project.

---

## Format Detection Pipeline

Format detection uses a four-stage pipeline, implemented in `Compression.Lib.FormatDetector`:

### Stage 1: Magic Bytes

The first bytes of the file are checked against all registered format signatures. Signatures are hash-indexed by their first bytes for O(n) lookup rather than linear scanning. Each `IFormatDescriptor` provides a `MagicSignatures` list with byte patterns and offsets.

### Stage 2: Trial Decompression

If magic bytes are ambiguous or absent, the `TrialDecompressor` (in `Compression.Analysis`) attempts all registered stream decompressors in parallel. Each decompressor runs in a try/catch with a timeout. A successful decompression that produces low-entropy output is a strong signal. Early termination occurs when a high-confidence match is found.

### Stage 3: Extension Fallback

If the above stages are inconclusive, the file extension is matched against all registered format extensions. Both single extensions (`.gz`) and compound extensions (`.tar.gz`, `.tgz`) are checked. Compound extensions are checked first for specificity.

### Stage 4: Deep Probing

Candidate formats from earlier stages are validated by parsing their headers and checking internal structure. For example, a ZIP candidate must have a valid local file header, and a TAR candidate must have a valid POSIX header with correct checksum.

---

## Source Generator

The Roslyn incremental source generator (`Compression.Registry.Generator.FormatDescriptorGenerator`) discovers all implementations of `IFormatDescriptor` and `IBuildingBlock` at compile time. It scans all referenced assemblies for public, non-abstract classes implementing these interfaces.

### Generated Output

The generator emits two files:

1. **FormatRegistration.g.cs** -- A partial class with `RegisterFormats()` and `RegisterBuildingBlocks()` methods containing explicit constructor calls for every discovered type:
   ```csharp
   static partial void RegisterFormats() {
     FormatRegistry.Register(new FileFormat.Zip.ZipFormatDescriptor());
     FormatRegistry.Register(new FileFormat.Gzip.GzipFormatDescriptor());
     // ... 180+ more
   }
   static partial void RegisterBuildingBlocks() {
     BuildingBlockRegistry.Register(new Compression.Core.BuildingBlocks.BB_Deflate());
     // ... 49 more
   }
   ```

2. **FormatDetector.Format.g.cs** -- An enum with one member per discovered format ID, plus special values (`Unknown`, `Sfx`, compound tar formats).

### Why Source Generation

- **Zero reflection at runtime** -- No assembly scanning, no `Activator.CreateInstance`
- **Zero manual registration** -- Adding a new format project and referencing it from `Compression.Lib` is enough
- **Compile-time verification** -- If a descriptor class doesn't compile, the build fails immediately
- **Trimming-safe** -- No reflection means the .NET trimmer can safely remove unused code in single-file publish

---

## How to Add a New Format

1. Create a new project `FileFormat.YourFormat` at the repository root.

2. Implement `IFormatDescriptor` with metadata (ID, display name, extensions, magic bytes, capabilities):
   ```csharp
   using Compression.Registry;

   namespace FileFormat.YourFormat;

   public sealed class YourFormatDescriptor : IFormatDescriptor {
     public string Id => "YourFormat";
     public string DisplayName => "Your Format";
     public FormatCategory Category => FormatCategory.Archive;
     public FormatCapabilities Capabilities => FormatCapabilities.Read | FormatCapabilities.Write;
     public string DefaultExtension => ".yf";
     public IReadOnlyList<string> Extensions => [".yf", ".yourformat"];
     public IReadOnlyList<string> CompoundExtensions => [];
     public IReadOnlyList<MagicSignature> MagicSignatures => [
       new([0x59, 0x46], 0) // "YF" at offset 0
     ];
     public IReadOnlyList<FormatMethodInfo> Methods => [];
     public string? TarCompressionFormatId => null;
   }
   ```

3. Implement `IStreamFormatOperations` (for compression streams) or `IArchiveFormatOperations` (for multi-file archives).

4. Add a `<ProjectReference>` to `Compression.Lib.csproj`.

5. Add the project to `CompressionWorkbench.slnx`.

The source generator automatically discovers the new descriptor at the next build. No other changes are needed -- format detection, the CLI, and the UI will all pick up the new format.

---

## How to Add a Building Block

Building blocks are raw algorithm primitives in `Compression.Core` that can be benchmarked independently of any file format.

1. Create a class implementing `IBuildingBlock` in `Compression.Core`:
   ```csharp
   using Compression.Registry;

   namespace Compression.Core.BuildingBlocks;

   public sealed class BB_YourAlgorithm : IBuildingBlock {
     public string Id => "BB_YourAlgorithm";
     public string DisplayName => "Your Algorithm";
     public string Description => "Brief description of what it does";
     public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

     public byte[] Compress(ReadOnlySpan<byte> data) {
       // Compress raw bytes, return compressed output
     }

     public byte[] Decompress(ReadOnlySpan<byte> data) {
       // Decompress raw bytes, return original data
     }
   }
   ```

2. The source generator discovers it automatically. No registration code needed.

The `Compress`/`Decompress` methods operate on raw byte data with no container format or headers. The benchmark tool and `cwb benchmark` command will automatically include the new block.

---

## Analysis Pipeline

The binary analysis engine lives in `Compression.Analysis` and provides several components that can be used independently or composed together.

### BinaryAnalyzer

The top-level orchestrator. Given a file or stream, it runs all analysis stages and produces an `AnalysisResult` containing signatures, fingerprints, entropy data, trial results, and chain reconstruction.

### SignatureScanner

Scans a byte buffer for known format signatures. Uses all magic signatures from `FormatRegistry` (auto-discovered from format descriptors). Returns a list of matches with offsets, format names, and confidence scores.

### AlgorithmFingerprinter

Computes statistical fingerprints (byte frequency distributions, bigram patterns, entropy profiles) and compares them against known algorithm profiles to identify what compression was used, even without valid headers.

### TrialDecompressor

Attempts all registered stream decompressors against the input data in parallel. Each trial runs with a timeout and in a try/catch. Results are ranked by confidence (successful decompression with valid output is high confidence).

### ChainReconstructor

Discovers layered compression chains (e.g., gzip wrapping bzip2 wrapping raw data). Recursively decompresses the output of successful trials until no further decompression succeeds.

### EntropyMap

Computes per-region Shannon entropy at multiple resolutions (64KB, 8KB, 1KB, 256B). Uses CUSUM binary segmentation for change-point detection and KL-divergence plus chi-square tests for boundary validation. Edge sharpening via 1D Canny-style gradient analysis identifies precise transitions between different data types.

### StreamingAnalyzer

Handles arbitrarily large files by reading in 64KB chunks. Computes entropy and byte statistics without materializing the full file. Returns per-chunk entropy profiles for the entire file.

---

## Reverse Engineering

### FormatReverser (Black-Box Tool Probing)

Orchestrates reverse engineering of an unknown tool's output format. Given an executable and argument template:

1. **ProbeGenerator** creates approximately 40 controlled inputs (empty, single byte, patterns, text, random, various sizes)
2. Each probe is run through the tool via `ExternalToolRunner`
3. **OutputCorrelator** cross-correlates all outputs to find common headers/footers, size fields, and payload structure
4. **CompressionIdentifier** analyzes the payload region by trying all building blocks and checking entropy
5. A summary report describes magic bytes, size fields, compression algorithm, filename storage, and determinism

### StaticFormatAnalyzer (Known-Content Analysis)

When archive files with known original content are available (but no tool):

1. Searches each archive for the original content stored verbatim (byte-for-byte match)
2. If not found verbatim, compresses the original with each building block and searches for the compressed form
3. Searches for the original filename in UTF-8 and UTF-16
4. Cross-correlates multiple samples to find common headers, footers, and size fields
5. Produces a structural map of the archive format

---

## SIMD Strategy

Performance-critical paths use hardware intrinsics with runtime detection and scalar fallbacks.

### CRC-32

- **SSE4.2**: `Sse42.Crc32` for CRC-32C (Castagnoli) -- hardware instruction, processes 8 bytes per cycle
- **PCLMULQDQ**: Carry-less multiplication for IEEE CRC-32 -- processes 64 bytes at a time using 4-way parallel folding
- **ARM**: `Crc32.ComputeCrc32` / `Crc32.ComputeCrc32C` on ARM64 platforms
- **Scalar fallback**: Slice-by-8 table lookup for platforms without hardware support

### Match Finding

- **Vector256**: Vectorized byte comparison for extending LZ matches beyond the initial hash chain hit. Processes 32 bytes at a time to find the longest match.

### Histogram / Byte Frequency

- **Vector256**: Vectorized byte frequency counting for entropy calculation. Bins bytes in parallel using SIMD gather/scatter patterns.

### RLE Scanning

- **Vector256**: Vectorized run-length detection. Compares consecutive 32-byte lanes to find run boundaries in a single instruction.

All SIMD code paths are guarded by runtime `IsSupported` checks (e.g., `Avx2.IsSupported`, `Sse42.IsSupported`, `Crc32.IsSupported`) so the same binary runs on any x64 or ARM64 machine.

---

## Memory Strategy

### ArrayPool

Hot-path compression and decompression methods rent buffers from `ArrayPool<byte>.Shared` instead of allocating new arrays. This reduces Gen0 GC pressure during bulk operations. Used in:

- DEFLATE compressor/decompressor
- LZ4 block compression
- Snappy compression
- Brotli decompression
- Archive extraction loops (entry buffers)

All `ArrayPool.Rent` calls are paired with `ArrayPool.Return` in `finally` blocks to prevent leaks.

### stackalloc

Small, fixed-size buffers (headers, checksums, lookup tables under 1KB) use `stackalloc` to avoid heap allocation entirely. Examples:

- Format magic byte buffers (typically 4-16 bytes)
- CRC lookup table slices
- Small Huffman code length arrays

`stackalloc` is never used inside loops (CA2014) to prevent stack overflow.
