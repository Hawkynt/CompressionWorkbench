# Compression.Lib

Umbrella library that re-exports all `FileFormat.*` projects and provides a unified API for archive operations.

## Key Components

| File | Description |
|------|-------------|
| `FormatDetector` | Identifies formats by extension and magic bytes |
| `ArchiveOperations` | Unified List/Extract/Create/Convert across all formats |
| `CompressionOptions` | Compression level, method, password, threading options |
| `MethodSpec` | Method+ system for optimal encoding (`deflate+`, `lzma+`, etc.) |
| `ParallelCompression` | Thread-parallel compression for ZIP and 7z solid blocks |
| `EntropyDetector` | Chi-square incompressibility detection (auto-store random/encrypted data) |
| `SfxBuilder` | Self-extracting archive builder |
| `PeOverlay` | PE executable overlay scanner for SFX detection |

## 3-Tier Conversion Model

| Tier | Strategy | Example |
|------|----------|---------|
| 1 | Bitstream transfer (zero decompression) | `gz` <-> `zlib`, `zip` <-> `gz` |
| 2 | Container restream (decompress wrapper only) | `tar.gz` -> `tar.xz` |
| 3 | Full recompress (extract + re-encode) | `zip` -> `7z` |

## Format Categories

- **Archive formats**: ZIP, RAR, 7z, TAR, CAB, LZH, ARJ, ARC, ZOO, ACE, SQX, CPIO, AR, WIM, RPM, DEB, Shar, PAK, HA, ZPAQ, StuffIt, SquashFS, CramFS, NSIS, Inno Setup, DMS, LZX, Compact Pro, Spark, LBR, UHARC, WAD, XAR, ALZip
- **Compression streams**: Gzip, BZip2, XZ, Zstd, LZ4, Brotli, Snappy, LZOP, Compress(.Z), LZMA, Lzip, Zlib, SZDD, KWAJ, PowerPacker, Squeeze, ICE Packer, RZIP, MacBinary, BinHex, PackBits, Yaz0, BriefLZ, RNC, RefPack, aPLib, LZFSE, Freeze
- **Compound**: tar.gz, tar.bz2, tar.xz, tar.zst, tar.lz4, tar.lz
- **Detection-only**: ISO 9660, UDF

## Visibility

All types are `internal` with `InternalsVisibleTo` for: `Compression.CLI`, `Compression.UI`, `Compression.Shell`, `Compression.Tests`, and the SFX stubs.
