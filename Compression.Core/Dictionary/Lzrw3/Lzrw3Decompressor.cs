namespace Compression.Core.Dictionary.Lzrw3;

/// <summary>
/// Decodes the LZRW3 control-word format produced by <see cref="Lzrw3Compressor"/>.
/// Reference: Ross N. Williams, http://ross.net/compression/lzrw3.html.
/// Mirrors the compressor's queued hash-table synchronization exactly (see remarks there):
/// a position's hash entry only becomes visible once its 3-byte window is fully decoded.
/// </summary>
public static class Lzrw3Decompressor {
  private const int HashTableSize = 4096;
  private const int MinMatch = 3;
  private const int ItemsPerGroup = 16;

  /// <summary>Decompresses an LZRW3 stream.</summary>
  /// <param name="data">The compressed byte stream.</param>
  /// <param name="originalLength">The exact length of the decompressed output.</param>
  /// <returns>The reconstructed original bytes.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength) {
    var output = new byte[originalLength];
    var hashTable = new int[Lzrw3Decompressor.HashTableSize];
    var pending = new Queue<int>();

    var op = 0;
    var ip = 0;

    while (op < originalLength) {
      var controlWord = (data[ip] << 8) | data[ip + 1];
      ip += 2;

      for (var i = 0; i < Lzrw3Decompressor.ItemsPerGroup && op < originalLength; ++i) {
        FlushPending(pending, hashTable, output, op);

        if ((controlWord & (1 << i)) != 0) {
          var code = (data[ip] << 8) | data[ip + 1];
          ip += 2;
          var length = (code >> 12) + Lzrw3Decompressor.MinMatch;
          var hashIndex = code & 0xFFF;

          var refPos = hashTable[hashIndex];
          var phraseStart = op;
          for (var j = 0; j < length; ++j)
            output[op + j] = output[refPos + j];
          op += length;

          if (phraseStart + Lzrw3Decompressor.MinMatch <= originalLength)
            pending.Enqueue(phraseStart);
        } else {
          var bytePos = op;
          output[op++] = data[ip++];
          if (bytePos + Lzrw3Decompressor.MinMatch <= originalLength)
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
    return (int)((uint)value * 2654435761u >> 20) & (Lzrw3Decompressor.HashTableSize - 1);
  }
}
