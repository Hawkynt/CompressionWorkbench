using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lz4;

/// <summary>
/// Compresses data using the LZ4 block format.
/// LZ4 is a fast LZ77 variant that uses a simple hash table for match finding
/// and a compact token format with no entropy coding.
/// </summary>
public static class Lz4BlockCompressor {
  /// <summary>
  /// Compresses the input data using LZ4 block format.
  /// </summary>
  /// <param name="source">The data to compress.</param>
  /// <returns>The compressed block data.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> source) {
    if (source.Length == 0)
      return [];

    // Worst case: each literal needs 1 token byte + 1 literal byte, plus length overflow
    var dest = new byte[LZ4_compressBound(source.Length)];
    var written = CompressCore(source, dest);
    return dest.AsSpan(0, written).ToArray();
  }

  /// <summary>
  /// Compresses into a destination buffer, returning bytes written.
  /// </summary>
  /// <param name="source">The data to compress.</param>
  /// <param name="dest">The destination buffer (must be large enough).</param>
  /// <returns>Number of bytes written to <paramref name="dest"/>.</returns>
  public static int Compress(ReadOnlySpan<byte> source, Span<byte> dest) =>
    CompressCore(source, dest);

  private static int LZ4_compressBound(int inputSize) =>
    inputSize + (inputSize / 255) + 16;

  private static int CompressCore(ReadOnlySpan<byte> src, Span<byte> dst) {
    var srcLen = src.Length;
    if (srcLen == 0)
      return 0;

    var hashTable = new int[Lz4Constants.HashTableSize];
    hashTable.AsSpan().Fill(-1);

    var dstPos = 0;
    var anchor = 0; // Start of current literal run
    var pos = 0;
    var matchLimit = srcLen - Lz4Constants.MfLimit;

    while (pos < matchLimit) {
      // Find a match
      var matchOffset = 0;
      var matchLength = 0;

      if (pos + 3 < srcLen) {
        var h = Hash4(src, pos);
        var candidate = hashTable[h];
        hashTable[h] = pos;

        if (candidate >= 0 && pos - candidate <= Lz4Constants.MaxDistance)
          // Check if we have a 4-byte match
          if (src[candidate] == src[pos] &&
            src[candidate + 1] == src[pos + 1] &&
            src[candidate + 2] == src[pos + 2] &&
            src[candidate + 3] == src[pos + 3]) {
            matchOffset = pos - candidate;
            matchLength = 4;
            // Extend match
            while (pos + matchLength < srcLen &&
              src[candidate + matchLength] == src[pos + matchLength])
              ++matchLength;
          }
      }

      if (matchLength < Lz4Constants.MinMatch) {
        ++pos;
        continue;
      }

      // Emit sequence: literal length + match
      var litLen = pos - anchor;
      dstPos = EmitSequence(dst, dstPos, src, anchor, litLen, matchOffset, matchLength);

      // Advance past the match
      // Insert hash entries for skipped positions
      var end = pos + matchLength;
      ++pos;
      while (pos < end && pos + 3 < srcLen) {
        hashTable[Hash4(src, pos)] = pos;
        ++pos;
      }
      pos = end;
      anchor = pos;
    }

    // Emit last literals
    var lastLitLen = srcLen - anchor;
    dstPos = EmitLastLiterals(dst, dstPos, src, anchor, lastLitLen);

    return dstPos;
  }

  private static int EmitSequence(Span<byte> dst, int dstPos,
      ReadOnlySpan<byte> src, int litStart, int litLen,
      int matchOffset, int matchLength) {
    // Token byte: high nibble = literal length, low nibble = match length - 4
    var mlCode = matchLength - Lz4Constants.MinMatch;

    var tokenLit = Math.Min(litLen, Lz4Constants.RunMask);
    var tokenMatch = Math.Min(mlCode, Lz4Constants.RunMask);
    dst[dstPos++] = (byte)((tokenLit << 4) | tokenMatch);

    // Literal length overflow
    if (litLen >= Lz4Constants.RunMask) {
      var remaining = litLen - Lz4Constants.RunMask;
      while (remaining >= 255) {
        dst[dstPos++] = 255;
        remaining -= 255;
      }
      dst[dstPos++] = (byte)remaining;
    }

    // Literal bytes
    src.Slice(litStart, litLen).CopyTo(dst[dstPos..]);
    dstPos += litLen;

    // Match offset (16-bit LE)
    BinaryPrimitives.WriteUInt16LittleEndian(dst[dstPos..], (ushort)matchOffset);
    dstPos += 2;

    // Match length overflow
    if (mlCode < Lz4Constants.RunMask)
      return dstPos;

    {
      var remaining = mlCode - Lz4Constants.RunMask;
      while (remaining >= 255) {
        dst[dstPos++] = 255;
        remaining -= 255;
      }
      dst[dstPos++] = (byte)remaining;
    }

    return dstPos;
  }

  private static int EmitLastLiterals(Span<byte> dst, int dstPos,
      ReadOnlySpan<byte> src, int litStart, int litLen) {
    var tokenLit = Math.Min(litLen, Lz4Constants.RunMask);
    dst[dstPos++] = (byte)(tokenLit << 4); // match length = 0 (no match)

    if (litLen >= Lz4Constants.RunMask) {
      var remaining = litLen - Lz4Constants.RunMask;
      while (remaining >= 255) {
        dst[dstPos++] = 255;
        remaining -= 255;
      }
      dst[dstPos++] = (byte)remaining;
    }

    src.Slice(litStart, litLen).CopyTo(dst[dstPos..]);
    dstPos += litLen;

    return dstPos;
  }

  private static int Hash4(ReadOnlySpan<byte> data, int pos) =>
    (int)((BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]) * 2654435761u)
          >> (32 - Lz4Constants.HashTableBits));
}
