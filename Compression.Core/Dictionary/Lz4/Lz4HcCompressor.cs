using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Lz4;

/// <summary>
/// LZ4 High Compression (HC) block compressor using hash chains for deeper match finding.
/// Produces the same block format as <see cref="Lz4BlockCompressor"/> but with better ratios.
/// </summary>
internal static class Lz4HcCompressor {
  /// <summary>
  /// Compresses using hash chains with the specified chain depth.
  /// </summary>
  internal static byte[] Compress(ReadOnlySpan<byte> source, int maxChainDepth) {
    if (source.Length == 0)
      return [];

    var dest = new byte[Lz4BlockCompressor.CompressBound(source.Length)];
    int written = CompressCore(source, dest, maxChainDepth);
    return dest.AsSpan(0, written).ToArray();
  }

  private static int CompressCore(ReadOnlySpan<byte> src, Span<byte> dst, int maxChainDepth) {
    int srcLen = src.Length;
    if (srcLen == 0)
      return 0;

    // Hash table: maps hash → most recent position
    var hashTable = new int[Lz4Constants.HashTableSize];
    hashTable.AsSpan().Fill(-1);

    // Chain table: for each position, stores the previous position with the same hash
    var chainTable = new int[srcLen];
    chainTable.AsSpan().Fill(-1);

    int dstPos = 0;
    int anchor = 0;
    int pos = 0;
    int matchLimit = srcLen - Lz4Constants.MfLimit;

    while (pos < matchLimit) {
      int bestOffset = 0;
      int bestLength = 0;

      if (pos + 3 < srcLen) {
        int h = Hash4(src, pos);

        // Insert current position into chain
        int prev = hashTable[h];
        hashTable[h] = pos;
        if (prev >= 0)
          chainTable[pos] = prev;

        // Walk the chain to find the best match
        int candidate = prev;
        int depth = maxChainDepth;
        while (candidate >= 0 && depth-- > 0 && pos - candidate <= Lz4Constants.MaxDistance) {
          // Check 4-byte prefix
          if (src[candidate] == src[pos] &&
              src[candidate + 1] == src[pos + 1] &&
              src[candidate + 2] == src[pos + 2] &&
              src[candidate + 3] == src[pos + 3]) {
            int len = 4;
            while (pos + len < srcLen && src[candidate + len] == src[pos + len])
              ++len;

            if (len > bestLength) {
              bestLength = len;
              bestOffset = pos - candidate;
            }
          }

          candidate = chainTable[candidate];
        }
      }

      if (bestLength < Lz4Constants.MinMatch) {
        ++pos;
        continue;
      }

      // Lazy matching: check if next position has a better match
      if (pos + 1 < matchLimit && bestLength < srcLen - pos - 1) {
        int h2 = Hash4(src, pos + 1);
        int prev2 = hashTable[h2];
        hashTable[h2] = pos + 1;
        if (prev2 >= 0)
          chainTable[pos + 1] = prev2;

        int candidate = prev2;
        int depth = maxChainDepth;
        int lazyBest = 0;
        while (candidate >= 0 && depth-- > 0 && (pos + 1) - candidate <= Lz4Constants.MaxDistance) {
          if (src[candidate] == src[pos + 1] &&
              src[candidate + 1] == src[pos + 2] &&
              src[candidate + 2] == src[pos + 3]) {
            int len = 3;
            while (pos + 1 + len < srcLen && src[candidate + len] == src[pos + 1 + len])
              ++len;
            if (len > lazyBest)
              lazyBest = len;
          }
          candidate = chainTable[candidate];
        }

        if (lazyBest > bestLength) {
          // Skip this position, advance to next
          ++pos;
          continue;
        }
      }

      // Emit sequence
      int litLen = pos - anchor;
      dstPos = Lz4BlockCompressor.EmitSequence(dst, dstPos, src, anchor, litLen, bestOffset, bestLength);

      // Insert hash entries for all positions in the match
      int end = pos + bestLength;
      pos += 2; // Already inserted pos and pos+1
      while (pos < end && pos + 3 < srcLen) {
        int h = Hash4(src, pos);
        int prev = hashTable[h];
        hashTable[h] = pos;
        if (prev >= 0)
          chainTable[pos] = prev;
        ++pos;
      }
      pos = end;
      anchor = pos;
    }

    // Emit last literals
    int lastLitLen = srcLen - anchor;
    dstPos = Lz4BlockCompressor.EmitLastLiterals(dst, dstPos, src, anchor, lastLitLen);

    return dstPos;
  }

  private static int Hash4(ReadOnlySpan<byte> data, int pos) =>
    (int)((BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]) * 2654435761u)
          >> (32 - Lz4Constants.HashTableBits));
}
