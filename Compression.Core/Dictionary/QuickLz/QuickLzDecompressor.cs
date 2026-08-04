namespace Compression.Core.Dictionary.QuickLz;

/// <summary>
/// Decodes the QuickLZ level-1 style control-word format produced by <see cref="QuickLzCompressor"/>.
/// Reference: http://www.quicklz.com/. Mirrors the compressor's queued hash-table synchronization
/// exactly (see remarks there): a position's hash entry only becomes visible once its 3-byte window
/// is fully decoded.
/// </summary>
public static class QuickLzDecompressor {
  private const int HashBits = 12;
  private const int HashSize = 1 << QuickLzDecompressor.HashBits;
  private const int MinMatch = 3;
  private const int MaxShortMatch = 17;
  private const int ControlWordBits = 32;

  /// <summary>Decompresses a QuickLZ stream.</summary>
  /// <param name="data">The compressed byte stream.</param>
  /// <param name="originalLength">The exact length of the decompressed output.</param>
  /// <returns>The reconstructed original bytes.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength) {
    var output = new byte[originalLength];
    var hashTable = new int[QuickLzDecompressor.HashSize];
    var pending = new Queue<int>();

    var op = 0;
    var ip = 0;

    while (op < originalLength) {
      uint controlWord = (uint)(data[ip] | (data[ip + 1] << 8) | (data[ip + 2] << 16) | (data[ip + 3] << 24));
      ip += 4;

      for (var bitIndex = 0; bitIndex < QuickLzDecompressor.ControlWordBits && op < originalLength; ++bitIndex) {
        FlushPending(pending, hashTable, output, op);

        if ((controlWord & (1u << bitIndex)) != 0) {
          var word = data[ip] | (data[ip + 1] << 8);
          ip += 2;
          var field = word & 0x0F;
          var hash = (word >> 4) & (QuickLzDecompressor.HashSize - 1);

          int length;
          if (field == 0x0F) {
            length = data[ip++] + QuickLzDecompressor.MaxShortMatch + 1;
          } else
            length = field + QuickLzDecompressor.MinMatch;

          var refPos = hashTable[hash];
          var phraseStart = op;
          for (var j = 0; j < length; ++j)
            output[op + j] = output[refPos + j];
          op += length;

          if (phraseStart + QuickLzDecompressor.MinMatch <= originalLength)
            pending.Enqueue(phraseStart);
        } else {
          var bytePos = op;
          output[op++] = data[ip++];
          if (bytePos + QuickLzDecompressor.MinMatch <= originalLength)
            pending.Enqueue(bytePos);
        }
      }
    }

    return output;
  }

  /// <summary>Commits queued hash-table updates whose 3-byte window has fully materialized in <paramref name="output"/>.</summary>
  private static void FlushPending(Queue<int> pending, int[] hashTable, byte[] output, int currentPos) {
    while (pending.Count > 0 && pending.Peek() + 2 < currentPos) {
      var p = pending.Dequeue();
      hashTable[Hash(output, p)] = p;
    }
  }

  private static int Hash(ReadOnlySpan<byte> data, int position) {
    var value = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
    return (value ^ (value >> 12)) & (QuickLzDecompressor.HashSize - 1);
  }
}
