namespace Compression.Core.Dictionary.Lzrw3;

/// <summary>
/// Implements LZRW3 compression from its public algorithm description:
/// Ross N. Williams, "LZRW3: A Hash Table Index Variant of LZRW1", http://ross.net/compression/lzrw3.html —
/// an LZRW1 derivative that transmits 4096-entry hash-table indices instead of raw offsets, so the
/// decoder can resolve a match purely from a synchronized hash table (a "persistent phrase" table)
/// rather than an explicit distance field.
/// </summary>
/// <remarks>
/// Because a match token carries a hash bucket index rather than a distance, the decoder's hash
/// table must reach the exact same state as the encoder's at every point in the stream. Updates are
/// therefore queued at the position they are discovered and only committed once the referenced 3-byte
/// window lies fully inside already-produced output — the same rule applies symmetrically on the
/// encoder side (even though it could resolve the window immediately) so that both sides observe
/// identical table contents when a match is searched or resolved.
/// </remarks>
public static class Lzrw3Compressor {
  private const int HashTableSize = 4096;
  private const int HashMask = Lzrw3Compressor.HashTableSize - 1;
  private const int MinMatch = 3;
  private const int MaxMatch = 18;
  private const int ItemsPerGroup = 16;

  /// <summary>Compresses <paramref name="data"/> using the LZRW3 control-word format.</summary>
  /// <param name="data">The uncompressed input bytes.</param>
  /// <returns>The LZRW3-encoded byte stream.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var n = data.Length;
    var output = new List<byte>(Math.Max(16, n));
    if (n == 0)
      return [];

    var hashTable = new int[Lzrw3Compressor.HashTableSize];
    hashTable.AsSpan().Fill(-1);
    var pending = new Queue<int>();

    var pos = 0;
    while (pos < n) {
      var controlWordPos = output.Count;
      output.Add(0);
      output.Add(0);
      var controlWord = 0;
      var itemCount = 0;

      while (itemCount < Lzrw3Compressor.ItemsPerGroup && pos < n) {
        FlushPending(pending, hashTable, data, pos);

        var matchLength = 0;
        var matchHash = -1;

        if (pos + Lzrw3Compressor.MinMatch <= n) {
          var hash = Hash(data, pos);
          var candidate = hashTable[hash];
          if (candidate >= 0 && candidate < pos)
            matchLength = MatchLength(data, candidate, pos, Math.Min(Lzrw3Compressor.MaxMatch, n - pos));
          if (matchLength >= Lzrw3Compressor.MinMatch)
            matchHash = hash;
          pending.Enqueue(pos);
        }

        if (matchLength >= Lzrw3Compressor.MinMatch) {
          controlWord |= 1 << itemCount;
          var code = ((matchLength - Lzrw3Compressor.MinMatch) << 12) | matchHash;
          output.Add((byte)(code >> 8));
          output.Add((byte)code);
          pos += matchLength;
        } else {
          output.Add(data[pos]);
          ++pos;
        }

        ++itemCount;
      }

      output[controlWordPos] = (byte)(controlWord >> 8);
      output[controlWordPos + 1] = (byte)controlWord;
    }

    return [.. output];
  }

  /// <summary>
  /// Commits queued hash-table updates whose 3-byte window is now fully available, using the same
  /// "available once <c>position + 2 &lt; currentPos</c>" rule the decompressor applies.
  /// </summary>
  private static void FlushPending(Queue<int> pending, int[] hashTable, ReadOnlySpan<byte> buffer, int currentPos) {
    while (pending.Count > 0 && pending.Peek() + 2 < currentPos) {
      var p = pending.Dequeue();
      hashTable[Hash(buffer, p)] = p;
    }
  }

  private static int Hash(ReadOnlySpan<byte> data, int position) {
    var value = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
    return (int)((uint)value * 2654435761u >> 20) & Lzrw3Compressor.HashMask;
  }

  private static int MatchLength(ReadOnlySpan<byte> data, int a, int b, int maxLength) {
    var len = 0;
    while (len < maxLength && data[a + len] == data[b + len])
      ++len;
    return len;
  }
}
