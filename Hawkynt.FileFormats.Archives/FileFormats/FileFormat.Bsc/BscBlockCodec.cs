using System.Buffers.Binary;
using Compression.Core.Transforms;

namespace FileFormat.Bsc;

/// <summary>
/// libbsc's 28-byte memory-block payload pipeline: optional LZP, BWT, QLFC and
/// the auxiliary-index trailer. The outer bsc1 file framing lives in <see cref="BscStream"/>.
/// </summary>
internal static class BscBlockCodec {
  internal const int BlockSorterBwt = 1;
  internal const int CoderQlfcStatic = 1;
  internal const int CoderQlfcAdaptive = 2;
  internal const int CoderQlfcFast = 3;
  internal const int DefaultLzpHashSize = 15;
  internal const int DefaultLzpMinimumLength = 72;

  internal readonly record struct EncodedBlock(
    byte[] Payload,
    int Mode,
    int PrimaryIndex,
    uint DataChecksum,
    BscSortingContexts SortingContexts);

  /// <summary>
  /// Encodes a logical file block. New output is always genuine libbsc syntax:
  /// BWT + the selected QLFC coder (optionally preceded by LZP), or mode 0 raw
  /// storage when the compressed representation would not be smaller.
  /// </summary>
  public static EncodedBlock Encode(
      ReadOnlySpan<byte> logicalData,
      BscSortingContexts requestedContexts,
      BscEntropyCoder entropyCoder) {
    if (logicalData.IsEmpty)
      throw new ArgumentException("BSC memory blocks cannot be empty", nameof(logicalData));
    if (entropyCoder is not (BscEntropyCoder.Static or BscEntropyCoder.Adaptive or BscEntropyCoder.Fast))
      throw new ArgumentOutOfRangeException(nameof(entropyCoder));

    var contextData = logicalData.ToArray();
    if (requestedContexts == BscSortingContexts.Preceding)
      Array.Reverse(contextData);

    // bsc_compress stores a block of 28 bytes or less as it is given, so a preceding-context
    // block is stored reversed and keeps its marker. A block that does not compress (below) is
    // stored by the bsc tool instead, from the original bytes and marked following.
    if (contextData.Length <= 28)
      return new(contextData, 0, 0, Adler32(contextData), requestedContexts);

    var lzpEnabled = BscLzp.TryCompress(
      contextData,
      DefaultLzpHashSize,
      DefaultLzpMinimumLength,
      out var lzpData);
    ReadOnlySpan<byte> bwtInput = lzpEnabled ? lzpData : contextData;

    // bsc_bwt_encode always samples auxiliary indexes (they let the inverse BWT run in parallel);
    // bsc_compress keeps them only for blocks of 64 KiB and more.
    var (bwtData, primaryIndex, samples) = SuffixBurrowsWheelerTransform.Forward(bwtInput, AuxiliaryIndexRate(bwtInput.Length));
    var auxiliaryIndexCount = logicalData.Length < 64 * 1024 ? 0 : (byte)((bwtInput.Length - 1) / AuxiliaryIndexRate(bwtInput.Length));
    var coderData = entropyCoder switch {
      BscEntropyCoder.Static => BscQlfcModel.Compress(bwtData, adaptive: false),
      BscEntropyCoder.Adaptive => BscQlfcModel.Compress(bwtData, adaptive: true),
      BscEntropyCoder.Fast => BscQlfcFast.Compress(bwtData),
      _ => throw new ArgumentOutOfRangeException(nameof(entropyCoder)),
    };
    if (coderData is null)
      return Store(logicalData);

    // libbsc appends the auxiliary indexes (zero-based, little-endian) and their one-byte count.
    var payload = new byte[coderData.Length + 4 * auxiliaryIndexCount + 1];
    coderData.CopyTo(payload, 0);
    for (var t = 0; t < auxiliaryIndexCount; ++t)
      BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(coderData.Length + 4 * t), samples[t + 1] - 1);
    payload[^1] = (byte)auxiliaryIndexCount;

    if (payload.Length >= logicalData.Length)
      return Store(logicalData);

    var mode = BlockSorterBwt | ((int)entropyCoder << 5);
    if (lzpEnabled)
      mode |= DefaultLzpMinimumLength << 8 | DefaultLzpHashSize << 16;

    return new(
      payload,
      mode,
      primaryIndex,
      Adler32(contextData),
      requestedContexts);
  }

  /// <summary>
  /// Decodes a standards-conforming libbsc memory block to the context-ordered
  /// data. The caller performs the outer file's preceding-context reversal.
  /// </summary>
  public static byte[] Decode(ReadOnlySpan<byte> payload, int mode, int primaryIndex, int dataSize) {
    if (dataSize < 0)
      throw new InvalidDataException("BSC: negative decoded data size");

    if (mode == 0) {
      if (primaryIndex != 0 || payload.Length != dataSize)
        throw new InvalidDataException("BSC: malformed stored block");
      return payload.ToArray();
    }

    if ((mode & unchecked((int)0xff000000)) != 0)
      throw new InvalidDataException($"BSC: unsupported mode bits 0x{mode:x8}");

    var blockSorter = mode & 0x1f;
    var coder = mode >> 5 & 0x7;
    var lzpMinimumLength = mode >> 8 & 0xff;
    var lzpHashSize = mode >> 16 & 0xff;

    if (blockSorter is not (BlockSorterBwt or >= 3 and <= 8))
      throw new InvalidDataException($"BSC: unknown block sorter {blockSorter}");
    if (coder is not (CoderQlfcStatic or CoderQlfcAdaptive or CoderQlfcFast))
      throw new NotSupportedException($"BSC: QLFC coder {coder} is not supported");
    if ((lzpMinimumLength == 0) != (lzpHashSize == 0))
      throw new InvalidDataException("BSC: incomplete LZP mode parameters");

    if (payload.IsEmpty)
      throw new InvalidDataException("BSC: missing compressed payload");
    var auxiliaryIndexCount = payload[^1];
    var auxiliaryBytes = checked(1 + auxiliaryIndexCount * sizeof(int));
    if (auxiliaryBytes > payload.Length)
      throw new InvalidDataException("BSC: truncated BWT auxiliary-index trailer");

    // Auxiliary indexes are an inverse-BWT acceleration only. Ignoring them and
    // using the primary index is explicitly supported by libbsc's own decoder.
    var coderPayload = payload[..^auxiliaryBytes];
    var bwtData = coder switch {
      CoderQlfcStatic => BscQlfcModel.Decompress(coderPayload, dataSize, adaptive: false),
      CoderQlfcAdaptive => BscQlfcModel.Decompress(coderPayload, dataSize, adaptive: true),
      CoderQlfcFast => BscQlfcFast.Decompress(coderPayload, dataSize),
      _ => throw new NotSupportedException($"BSC: QLFC coder {coder} is not supported"),
    };
    if (bwtData.Length == 0)
      throw new InvalidDataException("BSC: compressed block decoded to no BWT data");
    byte[] lzpOrOriginal;
    if (blockSorter == BlockSorterBwt) {
      if (primaryIndex <= 0 || primaryIndex > bwtData.Length)
        throw new InvalidDataException($"BSC: invalid one-based BWT primary index {primaryIndex}");
      lzpOrOriginal = SuffixBurrowsWheelerTransform.Inverse(bwtData, primaryIndex);
    } else {
      // Sort Transform of order 3..8: the index is zero-based.
      BscSortTransform.Decode(bwtData, blockSorter, primaryIndex);
      lzpOrOriginal = bwtData;
    }
    if (lzpHashSize == 0) {
      if (lzpOrOriginal.Length != dataSize)
        throw new InvalidDataException($"BSC: decoded {lzpOrOriginal.Length} bytes, expected {dataSize}");
      return lzpOrOriginal;
    }

    return BscLzp.Decompress(lzpOrOriginal, lzpHashSize, lzpMinimumLength, dataSize);
  }

  // bsc_bwt_encode's sampling rate: the largest power of two not above n / 8 (1 below 16 bytes).
  private static int AuxiliaryIndexRate(int n) {
    var mod = n / 8;
    mod |= mod >> 1;
    mod |= mod >> 2;
    mod |= mod >> 4;
    mod |= mod >> 8;
    mod |= mod >> 16;
    mod >>= 1;
    return mod + 1;
  }

  internal static uint Adler32(ReadOnlySpan<byte> data) {
    const uint modulus = 65521;
    uint a = 1;
    uint b = 0;

    // Chunking prevents the accumulators from growing unnecessarily large while
    // preserving the exact Adler-32 result used by libbsc.
    while (!data.IsEmpty) {
      var chunkLength = Math.Min(data.Length, 5552);
      foreach (var value in data[..chunkLength]) {
        a += value;
        b += a;
      }
      a %= modulus;
      b %= modulus;
      data = data[chunkLength..];
    }
    return b << 16 | a;
  }

  private static EncodedBlock Store(ReadOnlySpan<byte> logicalData) {
    var raw = logicalData.ToArray();
    return new(raw, 0, 0, Adler32(raw), BscSortingContexts.Following);
  }
}
