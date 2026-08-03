namespace Compression.Core.Dictionary.Zling;

/// <summary>
/// The LZ77 dictionary stage of the <see cref="ZlingBuildingBlock"/>: a bounded
/// hash-chain match finder over a sliding window, serialized as a flag-byte + payload
/// token stream (one flag bit per token, 8 tokens per group: 0 = literal byte follows,
/// 1 = match follows as 2-byte big-endian distance + 1-byte (length - MinMatch)).
/// The token stream is self-delimiting only in conjunction with the original
/// (decompressed) length, which the caller supplies out of band.
/// </summary>
internal static class ZlingLz {
  public const int MinMatch = 3;
  public const int MaxMatch = 258;
  private const int WindowSize = 32768;
  private const int MaxChain = 32;

  /// <summary>Runs the LZ77 stage, producing the flag/payload token stream.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    var n = data.Length;
    if (n == 0)
      return [];

    var output = new List<byte>();
    var payload = new List<byte>();
    var head = new Dictionary<int, int>();
    var prev = new int[n];

    var flagBits = 0;
    var flagCount = 0;

    void FlushGroup() {
      output.Add((byte)flagBits);
      output.AddRange(payload);
      payload.Clear();
      flagBits = 0;
      flagCount = 0;
    }

    void InsertHash(ReadOnlySpan<byte> d, int pos) {
      var key = Hash3(d, pos);
      prev[pos] = head.TryGetValue(key, out var h) ? h : -1;
      head[key] = pos;
    }

    var i = 0;
    while (i < n) {
      var matchLength = 0;
      var matchDistance = 0;

      if (i + MinMatch <= n) {
        var key = Hash3(data, i);
        if (head.TryGetValue(key, out var candidate)) {
          var chain = 0;
          while (candidate >= 0 && chain < MaxChain && i - candidate <= WindowSize) {
            var len = CommonPrefixLength(data, candidate, i, n);
            if (len > matchLength) {
              matchLength = len;
              matchDistance = i - candidate;
            }
            candidate = prev[candidate];
            chain++;
          }
        }
      }

      if (matchLength >= MinMatch) {
        var insertEnd = Math.Min(i + matchLength, n - MinMatch + 1);
        for (var p = i; p < insertEnd; p++)
          InsertHash(data, p);

        flagBits |= 1 << flagCount;
        payload.Add((byte)(matchDistance >> 8));
        payload.Add((byte)matchDistance);
        payload.Add((byte)(matchLength - MinMatch));
        flagCount++;
        i += matchLength;
      } else {
        if (i + MinMatch <= n)
          InsertHash(data, i);
        payload.Add(data[i]);
        flagCount++;
        i++;
      }

      if (flagCount == 8)
        FlushGroup();
    }

    if (flagCount > 0)
      FlushGroup();

    return [.. output];
  }

  /// <summary>Reverses the LZ77 stage, reconstructing exactly <paramref name="originalLength"/> bytes.</summary>
  public static byte[] Decode(ReadOnlySpan<byte> intermediate, int originalLength) {
    var result = new byte[originalLength];
    var outPos = 0;
    var pos = 0;

    while (outPos < originalLength) {
      var flags = intermediate[pos++];

      for (var bit = 0; bit < 8 && outPos < originalLength; bit++) {
        if (((flags >> bit) & 1) == 0) {
          result[outPos++] = intermediate[pos++];
          continue;
        }

        var hi = intermediate[pos++];
        var lo = intermediate[pos++];
        var lengthCode = intermediate[pos++];
        var distance = (hi << 8) | lo;
        var length = lengthCode + MinMatch;

        var src = outPos - distance;
        for (var k = 0; k < length; k++)
          result[outPos + k] = result[src + k];
        outPos += length;
      }
    }

    return result;
  }

  private static int Hash3(ReadOnlySpan<byte> data, int pos)
    => (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2];

  private static int CommonPrefixLength(ReadOnlySpan<byte> data, int a, int b, int n) {
    var max = Math.Min(MaxMatch, n - b);
    var len = 0;
    while (len < max && data[a + len] == data[b + len])
      len++;
    return len;
  }
}
