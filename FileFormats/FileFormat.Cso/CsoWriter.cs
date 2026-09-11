#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;

namespace FileFormat.Cso;

/// <summary>
/// Writes PSP CSO v1/v2 and ZSO compressed-ISO containers.
/// </summary>
/// <remarks>
/// CSO v1 uses raw DEFLATE and bit 31 as the stored/uncompressed flag. ZSO keeps the v1 index
/// semantics but uses raw LZ4 blocks. CSO v2 infers stored blocks from an indexed span greater
/// than or equal to block_size and uses bit 31 to select LZ4 (set) or DEFLATE (clear) for shorter
/// compressed spans.
/// </remarks>
public sealed class CsoWriter {
  /// <summary>Default block size — one cooked ISO 9660 sector.</summary>
  public const int DefaultBlockSize = 2048;

  /// <summary>CSO/ZSO header length in bytes.</summary>
  internal const int HeaderSize = 24;

  /// <summary>Bit 31 of an index entry: stored for v1/ZSO, LZ4-method for compressed CSO v2.</summary>
  internal const uint IndexUncompressedFlag = 0x8000_0000u;

  /// <summary>Mask for the shifted file-offset portion of an index entry.</summary>
  internal const uint IndexOffsetMask = 0x7FFF_FFFFu;

  /// <summary>Builds a canonical CSO v1 stream around <paramref name="uncompressedData"/>.</summary>
  public static byte[] Build(ReadOnlySpan<byte> uncompressedData, int blockSize = DefaultBlockSize)
    => Build(uncompressedData, blockSize, CsoVariant.CsoV1);

  internal static byte[] Build(ReadOnlySpan<byte> uncompressedData, int blockSize, CsoVariant variant) {
    using var input = new MemoryStream(uncompressedData.ToArray(), writable: false);
    using var output = new MemoryStream();
    Write(output, input, checked((ulong)uncompressedData.Length), blockSize, variant);
    return output.ToArray();
  }

  /// <summary>
  /// Re-encodes <paramref name="uncompressedInput"/> into a canonical align=0 CSO/ZSO stream
  /// without buffering the whole ISO in memory.
  /// </summary>
  internal static void Write(
      Stream output, Stream uncompressedInput, ulong uncompressedSize, int blockSize, CsoVariant variant) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(uncompressedInput);
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("CSO/ZSO output must be writable and seekable.", nameof(output));
    if (!uncompressedInput.CanRead)
      throw new ArgumentException("CSO/ZSO input must be readable.", nameof(uncompressedInput));
    if (blockSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(blockSize), "block_size must be positive.");

    var blockSize64 = checked((ulong)blockSize);
    var blockCount64 = uncompressedSize / blockSize64 + (uncompressedSize % blockSize64 == 0 ? 0UL : 1UL);
    if (blockCount64 > CsoImage.MaximumBlockCount)
      throw new InvalidOperationException($"CSO/ZSO block count is too large: {blockCount64}.");
    var blockCount = checked((int)blockCount64);
    var index = new uint[checked(blockCount + 1)];
    var dataStart = checked(HeaderSize + index.Length * sizeof(uint));

    output.Position = 0;
    output.SetLength(dataStart);
    output.Position = dataStart;

    var slab = new byte[blockSize];
    ulong consumed = 0;
    for (var i = 0; i < blockCount; ++i) {
      Array.Clear(slab);
      var logicalLength = checked((int)Math.Min(blockSize64, uncompressedSize - consumed));
      ReadExact(uncompressedInput, slab.AsSpan(0, logicalLength));
      consumed += checked((uint)logicalLength);

      var offset = CheckedIndexOffset(output.Position);
      var (payload, flag) = EncodeBlock(slab, variant);
      index[i] = offset | flag;
      output.Write(payload);
    }

    if (consumed != uncompressedSize)
      throw new InvalidDataException($"CSO/ZSO encoder consumed {consumed} bytes; expected {uncompressedSize}.");

    index[blockCount] = CheckedIndexOffset(output.Position);

    Span<byte> header = stackalloc byte[HeaderSize];
    WriteHeader(header, uncompressedSize, blockSize, variant);
    output.Position = 0;
    output.Write(header);
    Span<byte> entry = stackalloc byte[sizeof(uint)];
    foreach (var value in index) {
      BinaryPrimitives.WriteUInt32LittleEndian(entry, value);
      output.Write(entry);
    }
    output.Position = output.Length;
  }

  internal static void WriteHeader(Span<byte> target, ulong uncompressedSize, int blockSize, CsoVariant variant) {
    if (target.Length < HeaderSize)
      throw new ArgumentException("CSO/ZSO header target is too small.", nameof(target));
    target[..HeaderSize].Clear();

    var magic = variant == CsoVariant.Zso ? "ZISO"u8 : "CISO"u8;
    magic.CopyTo(target);
    BinaryPrimitives.WriteUInt32LittleEndian(target[4..8], HeaderSize);
    BinaryPrimitives.WriteUInt64LittleEndian(target[8..16], uncompressedSize);
    BinaryPrimitives.WriteUInt32LittleEndian(target[16..20], checked((uint)blockSize));
    target[20] = variant == CsoVariant.CsoV2 ? (byte)2 : (byte)1;
    target[21] = 0; // canonical writer emits byte-granular indexes (no padding/alignment shift).
  }

  /// <summary>Raw-DEFLATE encode <paramref name="data"/> (no zlib wrapper).</summary>
  internal static byte[] Deflate(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
      deflate.Write(data);
    return output.ToArray();
  }

  /// <summary>Raw-DEFLATE decode helper retained for callers that need a bounded decoded slab.</summary>
  internal static byte[] Inflate(ReadOnlySpan<byte> data, int expectedSize) {
    using var input = new MemoryStream(data.ToArray(), writable: false);
    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
    var output = new byte[expectedSize];
    var written = 0;
    while (written < output.Length) {
      var read = deflate.Read(output, written, output.Length - written);
      if (read <= 0)
        break;
      written += read;
    }
    if (written != output.Length)
      Array.Resize(ref output, written);
    return output;
  }

  private static (byte[] Payload, uint Flag) EncodeBlock(ReadOnlySpan<byte> slab, CsoVariant variant) {
    switch (variant) {
      case CsoVariant.CsoV1: {
        var compressed = Deflate(slab);
        return compressed.Length < slab.Length
          ? (compressed, 0u)
          : (slab.ToArray(), IndexUncompressedFlag);
      }
      case CsoVariant.Zso: {
        var compressed = Lz4BlockCodec.Compress(slab);
        return compressed.Length < slab.Length
          ? (compressed, 0u)
          : (slab.ToArray(), IndexUncompressedFlag);
      }
      case CsoVariant.CsoV2: {
        var deflate = Deflate(slab);
        var lz4 = Lz4BlockCodec.Compress(slab);
        if (lz4.Length < deflate.Length && lz4.Length < slab.Length)
          return (lz4, IndexUncompressedFlag);
        if (deflate.Length < slab.Length)
          return (deflate, 0u);
        // CSO v2 does not flag stored blocks: span >= block_size is the stored discriminator.
        return (slab.ToArray(), 0u);
      }
      default:
        throw new ArgumentOutOfRangeException(nameof(variant));
    }
  }

  private static uint CheckedIndexOffset(long position) {
    if (position < 0 || position > IndexOffsetMask)
      throw new InvalidOperationException(
        "CSO/ZSO output exceeds the 31-bit byte-offset range of an align=0 index.");
    return checked((uint)position);
  }

  private static void ReadExact(Stream input, Span<byte> destination) {
    var read = 0;
    while (read < destination.Length) {
      var count = input.Read(destination[read..]);
      if (count <= 0)
        throw new EndOfStreamException("Uncompressed CSO/ZSO input ended before uncompressed_size.");
      read += count;
    }
  }
}
