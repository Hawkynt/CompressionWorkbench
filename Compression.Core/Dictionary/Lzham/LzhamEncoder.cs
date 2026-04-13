namespace Compression.Core.Dictionary.Lzham;

/// <summary>
/// LZHAM encoder: LZ77 with Huffman-coded literals, lengths, and distances.
/// Inspired by Valve's LZHAM codec.
/// </summary>
public sealed class LzhamEncoder {
  private const int MinMatchLen = 3;
  private const int MaxMatchLen = 258;
  private const int WindowSize = 32768;
  private const int HashSize = 1 << 15;
  private const int HashMask = HashSize - 1;

  /// <summary>
  /// Compresses data using LZ77 + Huffman.
  /// Returns the serialized bitstream including embedded frequency tables.
  /// </summary>
  public byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return [];

    // LZ77 pass: produce token stream.
    var tokens = new List<(bool isMatch, byte lit, int len, int dist)>();
    var hashHead = new int[HashSize];
    var hashPrev = new int[data.Length];
    Array.Fill(hashHead, -1);

    var pos = 0;
    while (pos < data.Length) {
      var bestLen = 0;
      var bestDist = 0;

      if (pos + 2 < data.Length) {
        var h = Hash3(data, pos);
        var chainPos = hashHead[h];
        hashPrev[pos] = chainPos;
        hashHead[h] = pos;

        var chainLen = 0;
        while (chainPos >= 0 && chainLen < 64) {
          var dist = pos - chainPos;
          if (dist > WindowSize) break;

          var len = 0;
          var maxLen = Math.Min(MaxMatchLen, data.Length - pos);
          while (len < maxLen && data[chainPos + len] == data[pos + len])
            len++;

          if (len >= MinMatchLen && len > bestLen) {
            bestLen = len;
            bestDist = dist;
            if (bestLen == maxLen) break;
          }

          chainPos = hashPrev[chainPos];
          chainLen++;
        }
      }

      if (bestLen >= MinMatchLen) {
        tokens.Add((true, 0, bestLen, bestDist));
        for (var i = 1; i < bestLen && pos + i + 2 < data.Length; i++) {
          var h = Hash3(data, pos + i);
          hashPrev[pos + i] = hashHead[h];
          hashHead[h] = pos + i;
        }
        pos += bestLen;
      } else {
        if (pos + 2 < data.Length) {
          var h = Hash3(data, pos);
          hashPrev[pos] = hashHead[h];
          hashHead[h] = pos;
        }
        tokens.Add((false, data[pos], 0, 0));
        pos++;
      }
    }

    // Compute lit/len and distance frequencies.
    var litLenFreq = new int[286];
    var distFreq = new int[30];

    foreach (var (isMatch, lit, len, dist) in tokens) {
      if (isMatch) {
        litLenFreq[GetLengthCode(len)]++;
        distFreq[GetDistCode(dist)]++;
      } else {
        litLenFreq[lit]++;
      }
    }

    // Build code lengths from frequencies.
    var litLenCodeLen = BuildCodeLengths(litLenFreq, 15);
    var distCodeLen = BuildCodeLengths(distFreq, 15);

    // Build canonical codes.
    var litLenCodes = BuildCanonicalCodes(litLenCodeLen);
    var distCodes = BuildCanonicalCodes(distCodeLen);

    // Serialize.
    using var ms = new MemoryStream();
    var writer = new BitWriter(ms);

    // Write frequency tables (code lengths).
    foreach (var cl in litLenCodeLen)
      writer.WriteBits((uint)cl, 4);
    foreach (var cl in distCodeLen)
      writer.WriteBits((uint)cl, 4);

    // Write tokens.
    foreach (var (isMatch, lit, len, dist) in tokens) {
      if (isMatch) {
        var lc = GetLengthCode(len);
        writer.WriteCode(litLenCodes[lc]);
        var (_, extraBits, extraVal) = GetLengthExtra(len);
        if (extraBits > 0) writer.WriteBits((uint)extraVal, extraBits);

        var dc = GetDistCode(dist);
        writer.WriteCode(distCodes[dc]);
        var (_, dExtra, dVal) = GetDistExtra(dist);
        if (dExtra > 0) writer.WriteBits((uint)dVal, dExtra);
      } else {
        writer.WriteCode(litLenCodes[lit]);
      }
    }

    writer.Flush();
    return ms.ToArray();
  }

  internal static int GetLengthCode(int length) => length switch {
    >= 3 and <= 10 => 257 + length - 3,
    >= 11 and <= 18 => 265 + (length - 11) / 2,
    >= 19 and <= 34 => 269 + (length - 19) / 4,
    >= 35 and <= 66 => 273 + (length - 35) / 8,
    >= 67 and <= 130 => 277 + (length - 67) / 16,
    >= 131 and <= 257 => 281 + (length - 131) / 32,
    258 => 285,
    _ => throw new ArgumentOutOfRangeException(nameof(length))
  };

  internal static (int code, int extraBits, int extraVal) GetLengthExtra(int length) {
    var code = GetLengthCode(length);
    return code switch {
      >= 257 and <= 264 => (code, 0, 0),
      >= 265 and <= 268 => (code, 1, (length - 11) % 2),
      >= 269 and <= 272 => (code, 2, (length - 19) % 4),
      >= 273 and <= 276 => (code, 3, (length - 35) % 8),
      >= 277 and <= 280 => (code, 4, (length - 67) % 16),
      >= 281 and <= 284 => (code, 5, (length - 131) % 32),
      285 => (285, 0, 0),
      _ => throw new InvalidDataException($"Invalid code: {code}")
    };
  }

  internal static int GetDistCode(int distance) {
    var d = distance - 1;
    if (d == 0) return 0;
    if (d == 1) return 1;
    var bits = 0;
    var val = d;
    while (val >= 2) { val >>= 1; bits++; }
    return bits * 2 + ((d >> (bits - 1)) & 1);
  }

  internal static (int code, int extraBits, int extraVal) GetDistExtra(int distance) {
    var code = GetDistCode(distance);
    if (code <= 1) return (code, 0, 0);
    var extra = (code - 2) / 2;
    var baseDist = (2 + (code & 1)) << extra;
    return (code, extra, distance - 1 - baseDist);
  }

  internal static int[] BuildCodeLengths(int[] freq, int maxLen) {
    var n = freq.Length;
    // Simple length-limited Huffman: assign lengths based on frequency ranking.
    var indices = Enumerable.Range(0, n).Where(i => freq[i] > 0).OrderByDescending(i => freq[i]).ToList();
    var codeLens = new int[n];
    if (indices.Count == 0) return codeLens;
    if (indices.Count == 1) { codeLens[indices[0]] = 1; return codeLens; }

    // Package-merge approximation: assign lengths proportional to log of rank.
    var total = indices.Sum(i => (long)freq[i]);
    foreach (var idx in indices) {
      var p = (double)freq[idx] / total;
      var len = Math.Max(1, Math.Min(maxLen, (int)Math.Ceiling(-Math.Log2(p))));
      codeLens[idx] = len;
    }

    // Kraft inequality correction.
    KraftCorrect(codeLens, maxLen);
    return codeLens;
  }

  private static void KraftCorrect(int[] codeLens, int maxLen) {
    // Ensure sum of 2^-len <= 1.
    while (true) {
      var kraft = 0.0;
      for (var i = 0; i < codeLens.Length; i++)
        if (codeLens[i] > 0) kraft += Math.Pow(2, -codeLens[i]);
      if (kraft <= 1.0001) break;
      // Find shortest code and increase it.
      var minLen = int.MaxValue;
      var minIdx = -1;
      for (var i = 0; i < codeLens.Length; i++) {
        if (codeLens[i] > 0 && codeLens[i] < minLen) { minLen = codeLens[i]; minIdx = i; }
      }
      if (minIdx < 0 || codeLens[minIdx] >= maxLen) break;
      codeLens[minIdx]++;
    }
    // If Kraft sum < 1, reduce longest codes.
    while (true) {
      var kraft = 0.0;
      for (var i = 0; i < codeLens.Length; i++)
        if (codeLens[i] > 0) kraft += Math.Pow(2, -codeLens[i]);
      if (kraft >= 0.9999) break;
      var maxL = 0;
      var maxIdx = -1;
      for (var i = 0; i < codeLens.Length; i++) {
        if (codeLens[i] > maxL) { maxL = codeLens[i]; maxIdx = i; }
      }
      if (maxIdx < 0 || codeLens[maxIdx] <= 1) break;
      codeLens[maxIdx]--;
    }
  }

  internal static (uint code, int len)[] BuildCanonicalCodes(int[] codeLens) {
    var n = codeLens.Length;
    var codes = new (uint code, int len)[n];
    var maxLen = codeLens.Max();
    if (maxLen == 0) return codes;

    var blCount = new int[maxLen + 1];
    for (var i = 0; i < n; i++)
      if (codeLens[i] > 0) blCount[codeLens[i]]++;

    var nextCode = new uint[maxLen + 1];
    var c = 0u;
    for (var bits = 1; bits <= maxLen; bits++) {
      c = (c + (uint)blCount[bits - 1]) << 1;
      nextCode[bits] = c;
    }

    for (var i = 0; i < n; i++) {
      var len = codeLens[i];
      if (len > 0) {
        codes[i] = (nextCode[len], len);
        nextCode[len]++;
      }
    }
    return codes;
  }

  private static int Hash3(ReadOnlySpan<byte> data, int pos)
    => ((data[pos] << 10) ^ (data[pos + 1] << 5) ^ data[pos + 2]) & HashMask;

  internal sealed class BitWriter(Stream output) {
    private uint _buffer;
    private int _bitCount;

    public void WriteBits(uint value, int count) {
      for (var i = count - 1; i >= 0; i--) {
        _buffer = (_buffer << 1) | ((value >> i) & 1);
        _bitCount++;
        if (_bitCount == 8) { output.WriteByte((byte)_buffer); _buffer = 0; _bitCount = 0; }
      }
    }

    public void WriteCode((uint code, int len) c) => WriteBits(c.code, c.len);

    public void Flush() {
      if (_bitCount > 0) {
        _buffer <<= (8 - _bitCount);
        output.WriteByte((byte)_buffer);
        _buffer = 0;
        _bitCount = 0;
      }
    }
  }
}
