# Large inputs

What actually happens when a building block is handed hundreds of megabytes, where
the limits are, and what fails first beyond them. Every figure here was measured on
a 64-bit Windows host with 32 GB of RAM, .NET 10, Server GC.

## The hard ceiling: 2,147,483,591 bytes

`IBuildingBlock` is array-based:

```csharp
byte[] Compress(ReadOnlySpan<byte> data);
byte[] Decompress(ReadOnlySpan<byte> data);
```

`Array.MaxLength` is 2,147,483,591 — **57 bytes below 2^31**. Measured:

```
alloc byte[2.147.483.591] OK in 1 ms
alloc byte[2.147.483.592] FAILED: OutOfMemoryException: Array dimensions exceeded supported range.
```

So an input of 2^31 bytes cannot reach a building block through this interface at
all, and neither can an output of that size be returned. This is a property of the
interface, not of any algorithm. Raising it needs a streaming API — see
[Streaming](#streaming) below.

Below that ceiling, the practical limit is per-algorithm and is usually reached far
earlier, for one of three reasons: an algorithm that expands hits the array limit on
its *output*; a quadratic buffer strategy makes the run take longer than anyone will
wait; or a 32-bit quantity wraps.

## 32-bit quantities that wrap below the array limit

A bit count is the common case: `length * 8` stops fitting an `int` at 2^28 bytes
(268,435,456), which is only 256 MB. Every occurrence below has been widened to
`long`. None of these changes alters output below the size at which the old code
wrapped, because `(long)x * 8 == x * 8` exactly while `x * 8` still fits.

| Where | Quantity | Wraps at | What went wrong |
|---|---|---|---|
| `Entropy/UnaryBuildingBlock.cs` | decoder bit position | **16.7 MB of input** | `IndexOutOfRangeException` |
| `Analysis/ChainReconstruction/SuccessEvaluator.cs` | `inputData.Length * 100` | **20.5 MB** | every candidate rejected as "excessive expansion" |
| `Entropy/EliasDeltaBuildingBlock.cs` | decoder bit position | ~126 MB | `IndexOutOfRangeException` |
| `Entropy/EliasGammaBuildingBlock.cs` | decoder bit position | ~143 MB | `IndexOutOfRangeException` |
| `Entropy/LevenshteinBuildingBlock.cs` | decoder bit position | ~150 MB | `IndexOutOfRangeException` |
| `Entropy/TunstallBuildingBlock.cs` | decoder bit position | ~179 MB | `IndexOutOfRangeException` |
| `Entropy/GolombBuildingBlock.cs` | decoder bit position | depends on M; tens of MB | `IndexOutOfRangeException` |
| `Entropy/BpeBuildingBlock.cs` | `(prevLen - dataLen) * 200` | ~10.7 M pairs removed in one round | merge loop stops after one round — **silent ratio regression, no exception** |
| `Analysis/Fingerprinting/LzwHeuristic.cs` | `data.Length * 8` | 256 MB | LZW fingerprinting silently reports no match |
| `Analysis/TrialDecompression/TrialHuffman.cs` | `data.Length * 8` | 256 MB | trial silently reports failure |
| `Analysis/TrialDecompression/TrialDecompressor.cs` | `data.Length * 4` | 512 MB | every trial strategy silently reports failure |
| `Analysis/ChainReconstruction/ChainReconstructor.cs` | `current.Length * 4` | 512 MB | same |
| `Dictionary/Zip/ReduceDecoder.cs` | `compressed.Length * 8`, bit position | 256 MB compressed | **returns an all-zero buffer of the right length, silently** |
| `Dictionary/MsLzh/MsLzhDecompressor.cs` | `originalSize * 8 + 1024` | 256 MB | decode budget negative; spins, then `EndOfStreamException` |
| `Dictionary/Brotli/BrotliCompressor.cs` | whole-stream `BitLength()` | 256 MB of output | wrong byte-alignment padding on later uncompressed meta-blocks |
| `Dictionary/Brotli/BrotliBitReader.cs` | `BitPosition` | 256 MB compressed | wrong reported position |
| `Entropy/Fse/FseDecoder.cs` | `lastByteIndex * 8 + highBit` | 256 MB | negative start position; garbage or exception |
| `Dictionary/Lzs`, `Lzwl`, `Lzham`, `Sqx`, `Ibm842`, `Zip/ImplodeDecoder` | decoder bit positions | 256 MB compressed | `IndexOutOfRangeException` |
| `Entropy/ExpGolomb`, `Entropy/GolombRice` | decoder bit positions | 256 MB compressed | `IndexOutOfRangeException` |
| `Simd/SimdBitPack.cs` | `symbolCount * bitsPerSymbol` | 2^28 symbols at 8 bits | undersized or negative buffer size |

Two further cases were checked and found **not** reachable:

- `Deflate/DeflateCompressor.cs` and `Deflate/Deflate64Compressor.cs` compute
  `3 + numSubBlocks * 5 * 8 + dataArray.Length * 8`. `Write` and `Finish` cap a block
  at `DefaultBlockSize` (32768 bytes, or 131072 at Maximum level), so the product
  never exceeds about one million. Measured: BB_Deflate compresses 268,435,455 and
  268,435,457 bytes of English text to the same 0.85 % ratio, with no regression at
  the boundary. Both expressions have been widened to `long` anyway, so a future
  change to the block size cannot reintroduce the hazard.
- The `List.Sort`, `Dictionary` and `PriorityQueue` orderings in the Huffman builders
  use `long` weights throughout and do not depend on frequency magnitude.

## Worst-case output buffers that cannot be represented

Several compressors size their output buffer from a worst-case bound that exceeds
`Array.MaxLength` before the *input* does. These now compute the bound in 64-bit and
throw `NotSupportedException` naming the size, rather than wrapping to a negative
length and failing with an unrelated message:

| Block | Bound | Refuses above |
|---|---|---|
| LZO1X, LZO1X-999 | `n + n/255 + 32` | ~2,139,062,000 bytes |
| LZ4 block | `n + n/255 + 16` | ~2,139,062,142 bytes |
| Snappy | `10 + n + n/6 + 32` | ~1,840,700,269 bytes |
| LZMS | `2n + 1024` | ~1,073,741,283 bytes |
| CVSD, DFPWM (decode) | `8n` samples | 268,435,448 bytes of input |

## Measured ceilings per algorithm

At 4 MB of English text, 119 of 122 building blocks round-trip. The three that do not
are time-bound, not size-bound: `BB_Crush`, `BB_RePair` and `BB_Zopfli` all exceed a
90-second budget because their optimal parsers are super-linear.

Selected measurements at larger sizes (English text, one process each):

| Block | Input | Compressed | Ratio | Compress | Decompress | Peak RSS |
|---|---|---|---|---|---|---|
| BB_Deflate | 4 MB | 35,684 | 0.85 % | 0.2 s | 0.03 s | 108 MB |
| BB_Deflate | 64 MB | 570,929 | 0.85 % | 7.5 s | 0.24 s | 382 MB |
| BB_Deflate | 128 MB | 1,141,861 | 0.85 % | 27.8 s | 0.34 s | 649 MB |
| BB_Deflate | 256 MB | 2,283,717 | 0.85 % | 63.7 s | 0.44 s | 1258 MB |
| BB_Unary | 8 MB | 134,742,020 | 1606 % | 1.0 s | 1.1 s | 591 MB |
| BB_Unary | 64 MB | 1,077,936,132 | 1606 % | 7.6 s | 9.1 s | 5147 MB |

**The largest input that works today is 256 MB and above for the mainstream blocks**,
limited in practice by time rather than by correctness. What fails first beyond it:

1. **The expanding coders hit the array limit on their output.** Unary emits up to
   256 bits per input byte. At 128 MB of uniformly distributed input its output would
   need 2,155,872,264 bytes, past `Array.MaxLength`, so `Compress` throws. Elias Gamma,
   Elias Delta, Levenshtein and Golomb behave the same way at their own expansion
   ratios. **These are the lowest real ceilings in the collection.**
2. **`DeflateCompressor.Write` is quadratic in the input.** It appends every byte to a
   `List<byte>` and then drains it with `RemoveRange(0, blockSize)`, which moves the
   remaining bytes each time. At 256 MB that is roughly 1.1 TB of memcpy, and the
   measured compress time grows fourfold for each doubling of the input (7.5 s at
   64 MB, 27.8 s at 128 MB, 63.7 s at 256 MB). This is a performance limit, not a
   correctness one; it is flagged here, not fixed.
3. **`Entropy/FseBuildingBlock.cs` stores one `byte` per bit** in a `List<byte>`.
   A 2^28-byte incompressible input needs 2,147,483,648 elements — 57 more than
   `Array.MaxLength` — so it throws `OutOfMemoryException` at exactly 256 MB.
   Flagged, not fixed: it needs a packed accumulator.
4. **Peak memory runs at roughly 5x the input** for the mainstream blocks (1258 MB
   for a 256 MB input through BB_Deflate), because the input array, the `List<byte>`
   buffer, the token list and the output all coexist.

## Running the large-input tests

`Compression.Tests/BuildingBlockLargeInputTests.cs` is `[Explicit]` and carries the
category `LargeInput`, so it never runs by default.

```
dotnet test --filter "Category=LargeInput"
```

The default size is 268,435,457 bytes (2^28 + 1), just past the point where a bit
count stops fitting an `int`. Two environment variables control the run:

- `CW_LARGE_INPUT_BYTES` — input size in bytes.
- `CW_LARGE_INPUT_BLOCKS` — comma-separated block ids, to restrict the sweep.

```
set CW_LARGE_INPUT_BYTES=268435456
set CW_LARGE_INPUT_BLOCKS=BB_Deflate,BB_Lz4,BB_Brotli
dotnet test --filter "Category=LargeInput"
```

Two targeted cases run without configuration:

- `Unary_RoundTrips_WhereA32BitBitPositionWouldWrap` — 16.7 MB, a few seconds. Pins
  the lowest reachable overflow in the collection.
- `Deflate_RatioIsStableAcrossThe256MegabyteBoundary` — compresses 268,435,455 and
  268,435,457 bytes and asserts the ratio does not move.

Do not run the whole sweep at 256 MB casually: `BB_Crush`, `BB_RePair`, `BB_Zopfli`,
`BB_ContextTreeWeighting` and `BB_Neural` are super-linear and will take hours.

## Streaming

Supporting inputs at or above 2^31 bytes requires an interface change, because
`byte[]` cannot express such a size. This is a design decision for the project owner;
nothing here has been built towards it. Concretely it would take:

**Interface.** A second, optional contract alongside `IBuildingBlock`, for example
`IStreamingBuildingBlock` with `void Compress(Stream input, Stream output)` and
`void Decompress(Stream input, Stream output)`. Adding it as a separate interface
rather than changing `IBuildingBlock` keeps the 122 existing implementations, the
registry, the benchmarks and the committed vectors working unchanged; the harnesses
would use the streaming path when a block advertises it and the array path otherwise.

**Which blocks could support it cheaply.** The block-structured formats already have
the shape: DEFLATE and Deflate64 are literally written as `Write`/`Finish` over a
`Stream` and only lose it at the `Compress(ReadOnlySpan<byte>)` façade; LZ4 frame,
Snappy, bzip2, XZ/LZMA2, Brotli and the LZO family all work in independent blocks.
For those the work is replacing whole-input `List<byte>`/`byte[]` accumulators with
a fixed-size window and 64-bit positions — the same widening already done here, plus
a ring buffer.

**Which blocks could not, without redesign.** BWT, the suffix-tree and suffix-array
blocks, Re-Pair, Sequitur, Zopfli and Crush all need the whole input resident to
build their global structure. They would have to gain a blocking scheme, which
changes their output format and would invalidate the committed vectors.

**What it would cost beyond the code.** Every committed vector is a
`byte[] -> byte[]` pair; a streaming path needs its own equivalence test proving it
produces the identical bytes as the array path for inputs both can handle. The
cross-check against the JavaScript implementations compares whole arrays, so it
would need a chunked mode too. And the JavaScript side has a much lower ceiling than
.NET does (see the Cipher documentation), so streaming would make the two projects'
reachable sizes diverge by an order of magnitude, and the cross-check would no
longer be able to cover the range where only one of them works.
