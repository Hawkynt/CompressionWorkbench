# Compression.Core

Core compression primitives and algorithms library. Every algorithm is implemented from scratch in C# with no external compression source code.

## Modules

| Module | Description |
|--------|-------------|
| `BitIO` | Bit-level readers and writers (LSB/MSB-first), `BitBuffer` |
| `Checksums` | CRC-16, CRC-32, CRC-32C, Adler-32, xxHash32, BLAKE2b, SHA-1, SHA-256, MD5 |
| `Crypto` | AES-256-CBC/CTR, Blowfish CBC, PBKDF2, ZipCrypto |
| `DataStructures` | Sliding window, priority queue, trie, suffix tree |
| `Deflate` | Deflate/Deflate64 compressor and decompressor, Zopfli optimizer, MsZip |
| `Dictionary` | Static dictionaries (Brotli) |
| `Entropy` | Huffman coding, arithmetic/range coding, ANS/FSE, PPMd, context mixing, Golomb-Rice |
| `Progress` | Progress reporting abstractions |
| `Streams` | Stream wrappers (SubStream, ConcatStream, etc.) |
| `Transforms` | BWT, MTF, Delta, BCJ (x86/ARM/Thumb/PPC/SPARC/IA64), BCJ2, RLE |

## Design Principles

- **No external dependencies** for compression algorithms
- **Span-based APIs** for zero-allocation paths where possible
- **Generic bit order** via `BitReader<TOrder>` / `BitWriter<TOrder>` with `LsbBitOrder` / `MsbBitOrder`
- **InternalsVisibleTo** `Compression.Tests` for thorough unit testing of internal primitives

## Usage

All `FileFormat.*` projects reference this library and compose its primitives to implement specific format containers and algorithms.

```csharp
// Example: Deflate compress/decompress
var compressed = DeflateCompressor.Compress(data);
var original = DeflateDecompressor.Decompress(compressed);

// Example: CRC-32
var crc = Crc32.Compute(data);
```
