namespace Compression.Core.Dictionary.Lzo;

/// <summary>
/// LZO1X-999 compressor using hash chains and optimal parsing for best compression ratio.
/// Produces the same output format as <see cref="Lzo1xCompressor"/>.
/// </summary>
internal static class Lzo1x999Compressor {
  private const int HashBits = 14;
  private const int HashSize = 1 << HashBits;
  private const int MinMatch = 4;
  private const int MaxDistance = 65535;
  private const int MaxChainDepth = 64;

  internal static byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return [];

    var output = new byte[data.Length + data.Length / 255 + 32];
    int outPos = 0;

    // Hash table + chain table for deep match finding
    int[] hashTable = new int[HashSize];
    hashTable.AsSpan().Fill(-1);
    int[] chainTable = new int[data.Length];
    chainTable.AsSpan().Fill(-1);

    // Optimal parsing: cost[i] = minimum coded bytes to encode data[0..i)
    int[] cost = new int[data.Length + 1];
    int[] prev = new int[data.Length + 1]; // previous position in optimal path
    int[] matchLen = new int[data.Length + 1]; // 0=literal, >0=match length at this step
    int[] matchDist = new int[data.Length + 1]; // match distance

    Array.Fill(cost, int.MaxValue);
    cost[0] = 0;

    int limit = data.Length - MinMatch;

    for (int pos = 0; pos < data.Length; ++pos) {
      if (cost[pos] == int.MaxValue)
        continue;

      // Option 1: literal byte — cost depends on current literal run
      int litCost = 1; // 1 byte for the literal
      int nextPos = pos + 1;
      if (nextPos <= data.Length && cost[pos] + litCost < cost[nextPos]) {
        cost[nextPos] = cost[pos] + litCost;
        prev[nextPos] = pos;
        matchLen[nextPos] = 0;
      }

      // Option 2: matches at this position
      if (pos <= limit) {
        int h = Hash4(data, pos);
        int candidate = hashTable[h];
        hashTable[h] = pos;
        if (candidate >= 0)
          chainTable[pos] = candidate;

        int depth = MaxChainDepth;
        while (candidate >= 0 && depth-- > 0 && pos - candidate <= MaxDistance) {
          // Check 4-byte prefix
          if (data[candidate] == data[pos] &&
              data[candidate + 1] == data[pos + 1] &&
              data[candidate + 2] == data[pos + 2] &&
              data[candidate + 3] == data[pos + 3]) {
            int len = 4;
            while (pos + len < data.Length && data[candidate + len] == data[pos + len])
              ++len;

            int dist = pos - candidate;
            // Cost of emitting this match: token overhead
            int mCost = MatchCost(len);
            int target = pos + len;
            if (target <= data.Length && cost[pos] + mCost < cost[target]) {
              cost[target] = cost[pos] + mCost;
              prev[target] = pos;
              matchLen[target] = len;
              matchDist[target] = dist;
            }
          }
          candidate = chainTable[candidate];
        }
      }
    }

    // Trace back the optimal path
    var tokens = new List<(int litStart, int litLen, int matchLength, int distance)>();
    int cur = data.Length;
    var rawTokens = new List<(int pos, int mLen, int mDist)>();
    while (cur > 0) {
      int p = prev[cur];
      int ml = matchLen[cur];
      int md = matchDist[cur];
      rawTokens.Add((p, ml, md));
      cur = p;
    }
    rawTokens.Reverse();

    // Group into (literal run + match) sequences
    int litAnchor = 0;
    for (int i = 0; i < rawTokens.Count; ++i) {
      var (tPos, tLen, tDist) = rawTokens[i];
      if (tLen == 0) {
        // Literal - just advance
        continue;
      }
      // Emit literals from litAnchor to tPos, then the match
      int litLen = tPos - litAnchor;
      outPos = EmitSequence(output, outPos, data, litAnchor, litLen, tDist, tLen);
      litAnchor = tPos + tLen;
    }

    // Final literal run
    int finalLitLen = data.Length - litAnchor;
    outPos = EmitFinalLiterals(output, outPos, data, litAnchor, finalLitLen);

    return output[..outPos];
  }

  private static int MatchCost(int matchLen) {
    // Token byte (1) + offset (2) = 3 bytes base
    // Plus extension bytes for matchLen > 19 (4 + 15)
    int extra = matchLen - MinMatch;
    int cost = 3;
    if (extra >= 15)
      cost += 1 + (extra - 15) / 255;
    return cost;
  }

  private static int EmitSequence(byte[] output, int outPos,
      ReadOnlySpan<byte> src, int litStart, int litLen, int distance, int matchLength) {
    int matchExtra = matchLength - MinMatch;
    int litNibble = Math.Min(litLen, 15);
    int matchNibble = Math.Min(matchExtra, 15);
    output[outPos++] = (byte)((litNibble << 4) | matchNibble);

    if (litNibble == 15) {
      int remaining = litLen - 15;
      while (remaining >= 255) { output[outPos++] = 255; remaining -= 255; }
      output[outPos++] = (byte)remaining;
    }

    src.Slice(litStart, litLen).CopyTo(output.AsSpan(outPos));
    outPos += litLen;

    output[outPos++] = (byte)(distance & 0xFF);
    output[outPos++] = (byte)(distance >> 8);

    if (matchNibble == 15) {
      int remaining = matchExtra - 15;
      while (remaining >= 255) { output[outPos++] = 255; remaining -= 255; }
      output[outPos++] = (byte)remaining;
    }

    return outPos;
  }

  private static int EmitFinalLiterals(byte[] output, int outPos,
      ReadOnlySpan<byte> src, int litStart, int litLen) {
    int litNibble = Math.Min(litLen, 15);
    output[outPos++] = (byte)(litNibble << 4);

    if (litNibble == 15) {
      int remaining = litLen - 15;
      while (remaining >= 255) { output[outPos++] = 255; remaining -= 255; }
      output[outPos++] = (byte)remaining;
    }

    src.Slice(litStart, litLen).CopyTo(output.AsSpan(outPos));
    outPos += litLen;

    return outPos;
  }

  private static int Hash4(ReadOnlySpan<byte> data, int pos) {
    uint v = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
    return (int)((v * 2654435761u) >> (32 - HashBits));
  }
}
