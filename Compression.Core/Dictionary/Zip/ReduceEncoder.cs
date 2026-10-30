namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Encodes data with ZIP Reduce (methods 2-5): LZ77 back-references escaped by byte 144 inside a
/// byte stream, which is then coded through per-predecessor follower sets.
/// </summary>
/// <remarks>
/// The stream layout, the follower-set cost model and the minimum match of four bytes (so the byte
/// after the escape is never 0) follow hwreduce in hwzip 2.4's reduce.c (Hans Wennborg, public
/// domain), see THIRD-PARTY-NOTICE.hwzip.txt. Follower sets are chosen from the whole byte stream
/// rather than its first 64 KB.
/// </remarks>
public static class ReduceEncoder {
  private const int MinMatch = 4;
  private const int HashBits = 14;
  private const int MaxChain = 64;

  /// <summary>
  /// Compresses data using the ZIP Reduce algorithm.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="factor">The compression factor, 1-4 (method 2-5).</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data, int factor = 4) {
    ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(factor, 4);

    var lengthBits = 8 - factor;
    var lengthMask = (1 << lengthBits) - 1;
    var maxLength = lengthMask + 255 + 3;
    var maxDistance = ((1 << factor) - 1) * 256 + 255 + 1;
    var stream = ToByteStream(data, lengthBits, lengthMask, maxLength, maxDistance);

    // Follower counts over the byte stream, the first predecessor being zero.
    var counts = new int[256, 256];
    byte previous = 0;
    foreach (var b in stream) {
      ++counts[previous, b];
      previous = b;
    }

    var writer = new ZipLegacyBitWriter(stream.Count + 1024);
    var followerIndex = new int[256, 256];
    var indexBits = new int[256];
    for (var current = 255; current >= 0; --current) {
      var order = Enumerable.Range(0, 256).OrderByDescending(b => counts[current, b]).ThenBy(b => b).ToArray();
      var bestSize = 0;
      var bestCost = Cost(counts, current, order, 0);
      for (var size = 1; size <= 32; ++size) {
        var cost = Cost(counts, current, order, size);
        if (cost < bestCost) {
          bestCost = cost;
          bestSize = size;
        }
      }

      for (var b = 0; b < 256; ++b)
        followerIndex[current, b] = -1;
      for (var i = 0; i < bestSize; ++i)
        followerIndex[current, order[i]] = i;

      indexBits[current] = ReduceDecoder.IndexBits(bestSize);
      writer.Write((ulong)bestSize, 6);
      for (var i = 0; i < bestSize; ++i)
        writer.Write((ulong)order[i], 8);
    }

    previous = 0;
    foreach (var b in stream) {
      var index = followerIndex[previous, b];
      var bits = indexBits[previous];
      if (index >= 0)
        writer.Write((ulong)index << 1, bits + 1);
      else if (bits != 0)
        writer.Write((ulong)b << 1 | 1, 9);
      else
        writer.Write(b, 8);

      previous = b;
    }

    return writer.ToArray();
  }

  private static long Cost(int[,] counts, int current, int[] order, int size) {
    long cost = size * 8L;
    var bits = ReduceDecoder.IndexBits(size);
    for (var i = 0; i < 256; ++i) {
      var count = counts[current, order[i]];
      cost += i < size ? count * (1L + bits) : count * (size == 0 ? 8L : 9L);
    }

    return cost;
  }

  /// <summary>Literals (144 doubled as 144, 0) and back-references (144, V, [extra], W).</summary>
  private static List<byte> ToByteStream(ReadOnlySpan<byte> data, int lengthBits, int lengthMask, int maxLength, int maxDistance) {
    var stream = new List<byte>(data.Length + data.Length / 16);
    var head = new int[1 << HashBits];
    Array.Fill(head, -1);
    var prev = new int[data.Length];
    var pos = 0;
    while (pos < data.Length) {
      var bestLength = 0;
      var bestDistance = 0;
      if (pos + MinMatch <= data.Length) {
        var steps = 0;
        for (var candidate = head[Hash(data, pos)]; candidate >= 0 && pos - candidate <= maxDistance && steps < MaxChain; candidate = prev[candidate], ++steps) {
          // Reduce back-references may not overlap the bytes they produce.
          var room = Math.Min(Math.Min(maxLength, data.Length - pos), pos - candidate);
          var length = 0;
          while (length < room && data[candidate + length] == data[pos + length])
            ++length;

          if (length > bestLength) {
            bestLength = length;
            bestDistance = pos - candidate;
          }
        }
      }

      int advance;
      if (bestLength >= MinMatch) {
        var length = bestLength - 3;
        var distance = bestDistance - 1;
        stream.Add(ReduceDecoder.Dle);
        stream.Add((byte)(Math.Min(length, lengthMask) | (distance >> 8) << lengthBits));
        if (length >= lengthMask)
          stream.Add((byte)(length - lengthMask));

        stream.Add((byte)distance);
        advance = bestLength;
      } else {
        stream.Add(data[pos]);
        if (data[pos] == ReduceDecoder.Dle)
          stream.Add(0);

        advance = 1;
      }

      for (var end = pos + advance; pos < end; ++pos)
        if (pos + 3 <= data.Length) {
          var h = Hash(data, pos);
          prev[pos] = head[h];
          head[h] = pos;
        }
    }

    return stream;
  }

  private static int Hash(ReadOnlySpan<byte> data, int pos)
    => (int)(((uint)(data[pos] << 16 | data[pos + 1] << 8 | data[pos + 2]) * 2654435761u) >> (32 - HashBits));
}
