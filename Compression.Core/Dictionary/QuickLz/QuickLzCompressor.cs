namespace Compression.Core.Dictionary.QuickLz;

/// <summary>
/// Implements a QuickLZ level-1 style compressor from the public algorithm description:
/// Lasse Mikkel Reinhold, "QuickLZ — fast compression library", http://www.quicklz.com/ —
/// a hash-matched LZ77 variant with a 32-bit control word (one bit per token) and matches
/// that reference a 4096-entry hash table by bucket index rather than by raw distance.
/// </summary>
/// <remarks>
/// As with LZRW3's index-based matches, the decoder cannot resolve a forward-looking 3-byte
/// hash the instant a token is produced (the trailing bytes of that window may not exist yet).
/// Both compressor and decompressor therefore queue candidate hash-table insertions and commit
/// them only once their 3-byte window is fully available, keeping both tables byte-for-byte
/// identical at every point in the stream.
/// </remarks>
public static class QuickLzCompressor {
  private const int HashBits = 12;
  private const int HashSize = 1 << QuickLzCompressor.HashBits;
  private const int HashMask = QuickLzCompressor.HashSize - 1;
  private const int MinMatch = 3;
  private const int MaxShortMatch = 17;
  private const int MaxMatch = 18 + 255;
  private const int ControlWordBits = 32;

  /// <summary>Compresses <paramref name="data"/> using the QuickLZ level-1 control-word format.</summary>
  /// <param name="data">The uncompressed input bytes.</param>
  /// <returns>The QuickLZ-encoded byte stream.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) {
    var n = data.Length;
    var output = new List<byte>(Math.Max(16, n));
    if (n == 0)
      return [];

    var hashTable = new int[QuickLzCompressor.HashSize];
    hashTable.AsSpan().Fill(-1);
    var pending = new Queue<int>();

    var controlWordPos = -1;
    uint controlWord = 0;
    var bitIndex = QuickLzCompressor.ControlWordBits;

    var pos = 0;
    while (pos < n) {
      if (bitIndex == QuickLzCompressor.ControlWordBits) {
        if (controlWordPos >= 0)
          WriteU32LE(output, controlWordPos, controlWord);
        controlWordPos = output.Count;
        output.Add(0); output.Add(0); output.Add(0); output.Add(0);
        controlWord = 0;
        bitIndex = 0;
      }

      FlushPending(pending, hashTable, data, pos);

      var matchLength = 0;
      var matchHash = -1;

      if (pos + QuickLzCompressor.MinMatch <= n) {
        var hash = Hash(data, pos);
        var candidate = hashTable[hash];
        if (candidate >= 0 && candidate < pos)
          matchLength = MatchLength(data, candidate, pos, Math.Min(QuickLzCompressor.MaxMatch, n - pos));
        if (matchLength >= QuickLzCompressor.MinMatch)
          matchHash = hash;
        pending.Enqueue(pos);
      }

      if (matchLength >= QuickLzCompressor.MinMatch) {
        controlWord |= 1u << bitIndex;
        EmitMatch(output, matchHash, matchLength);
        pos += matchLength;
      } else {
        output.Add(data[pos]);
        ++pos;
      }

      ++bitIndex;
    }

    if (controlWordPos >= 0)
      WriteU32LE(output, controlWordPos, controlWord);

    return [.. output];
  }

  /// <summary>Commits queued hash-table updates whose 3-byte window is now fully available in <paramref name="buffer"/>.</summary>
  private static void FlushPending(Queue<int> pending, int[] hashTable, ReadOnlySpan<byte> buffer, int currentPos) {
    while (pending.Count > 0 && pending.Peek() + 2 < currentPos) {
      var p = pending.Dequeue();
      hashTable[Hash(buffer, p)] = p;
    }
  }

  private static void EmitMatch(List<byte> output, int hash, int length) {
    if (length <= QuickLzCompressor.MaxShortMatch) {
      var word = (hash << 4) | (length - QuickLzCompressor.MinMatch);
      output.Add((byte)word);
      output.Add((byte)(word >> 8));
    } else {
      var word = (hash << 4) | 0x0F;
      output.Add((byte)word);
      output.Add((byte)(word >> 8));
      output.Add((byte)(length - (QuickLzCompressor.MaxShortMatch + 1)));
    }
  }

  private static void WriteU32LE(List<byte> output, int pos, uint value) {
    output[pos] = (byte)value;
    output[pos + 1] = (byte)(value >> 8);
    output[pos + 2] = (byte)(value >> 16);
    output[pos + 3] = (byte)(value >> 24);
  }

  private static int Hash(ReadOnlySpan<byte> data, int position) {
    var value = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
    return (value ^ (value >> 12)) & QuickLzCompressor.HashMask;
  }

  private static int MatchLength(ReadOnlySpan<byte> data, int a, int b, int maxLength) {
    var len = 0;
    while (len < maxLength && data[a + len] == data[b + len])
      ++len;
    return len;
  }
}
