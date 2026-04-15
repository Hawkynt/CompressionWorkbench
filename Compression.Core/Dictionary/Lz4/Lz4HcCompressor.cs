using System.Buffers;
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

    var destLen = Lz4BlockCompressor.CompressBound(source.Length);
    var dest = ArrayPool<byte>.Shared.Rent(destLen);
    try {
      var written = CompressCore(source, dest, maxChainDepth);
      return dest.AsSpan(0, written).ToArray();
    } finally {
      ArrayPool<byte>.Shared.Return(dest);
    }
  }

  private static int CompressCore(ReadOnlySpan<byte> src, Span<byte> dst, int maxChainDepth) {
    var srcLen = src.Length;
    if (srcLen == 0)
      return 0;

    // Hash table: maps hash → most recent position
    var hashTable = ArrayPool<int>.Shared.Rent(Lz4Constants.HashTableSize);
    // Chain table: for each position, stores the previous position with the same hash
    var chainTable = ArrayPool<int>.Shared.Rent(srcLen);
    try {
      hashTable.AsSpan(0, Lz4Constants.HashTableSize).Fill(-1);
      chainTable.AsSpan(0, srcLen).Fill(-1);

      var dstPos = 0;
      var anchor = 0;
      var pos = 0;
      var matchLimit = srcLen - Lz4Constants.MfLimit;

      while (pos < matchLimit) {
        var bestOffset = 0;
        var bestLength = 0;

        if (pos + 3 < srcLen) {
          var h = Hash4(src, pos);

          // Insert current position into chain
          var prev = hashTable[h];
          hashTable[h] = pos;
          if (prev >= 0)
            chainTable[pos] = prev;

          // Walk the chain to find the best match
          var candidate = prev;
          var depth = maxChainDepth;
          while (candidate >= 0 && depth-- > 0 && pos - candidate <= Lz4Constants.MaxDistance) {
            // Check 4-byte prefix
            if (src[candidate] == src[pos] &&
                src[candidate + 1] == src[pos + 1] &&
                src[candidate + 2] == src[pos + 2] &&
                src[candidate + 3] == src[pos + 3]) {
              var len = 4;
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
          var h2 = Hash4(src, pos + 1);
          var prev2 = hashTable[h2];
          hashTable[h2] = pos + 1;
          if (prev2 >= 0)
            chainTable[pos + 1] = prev2;

          var candidate = prev2;
          var depth = maxChainDepth;
          var lazyBest = 0;
          while (candidate >= 0 && depth-- > 0 && (pos + 1) - candidate <= Lz4Constants.MaxDistance) {
            if (src[candidate] == src[pos + 1] &&
                src[candidate + 1] == src[pos + 2] &&
                src[candidate + 2] == src[pos + 3]) {
              var len = 3;
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
        var litLen = pos - anchor;
        dstPos = Lz4BlockCompressor.EmitSequence(dst, dstPos, src, anchor, litLen, bestOffset, bestLength);

        // Insert hash entries for all positions in the match
        var end = pos + bestLength;
        pos += 2; // Already inserted pos and pos+1
        while (pos < end && pos + 3 < srcLen) {
          var h = Hash4(src, pos);
          var prev = hashTable[h];
          hashTable[h] = pos;
          if (prev >= 0)
            chainTable[pos] = prev;
          ++pos;
        }
        pos = end;
        anchor = pos;
      }

      // Emit last literals
      var lastLitLen = srcLen - anchor;
      dstPos = Lz4BlockCompressor.EmitLastLiterals(dst, dstPos, src, anchor, lastLitLen);

      return dstPos;
    } finally {
      ArrayPool<int>.Shared.Return(hashTable);
      ArrayPool<int>.Shared.Return(chainTable);
    }
  }

  private static int Hash4(ReadOnlySpan<byte> data, int pos) =>
    (int)((BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]) * 2654435761u)
          >> (32 - Lz4Constants.HashTableBits));
}
