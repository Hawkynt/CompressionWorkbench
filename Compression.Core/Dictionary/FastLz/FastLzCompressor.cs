using System.Buffers.Binary;

namespace Compression.Core.Dictionary.FastLz;

/// <summary>
/// FastLZ compression exactly as Ariya Hidayat's FastLZ library performs it, at level 1 and
/// level 2.
/// </summary>
/// <remarks>
/// <para>
/// Converted from FastLZ <c>fastlz.c</c> (MIT; see THIRD-PARTY-NOTICE.FastLZ.txt):
/// <c>fastlz1_compress</c>, <c>fastlz2_compress</c> and <c>fastlz_compress</c>'s level choice.
/// </para>
/// <para>
/// The top three bits of the first byte hold the level minus one. Then come runs:
/// <c>000LLLLL</c> and L+1 literals, or a match <c>LLLooooo [L'] oooooooo</c> copying L+2 bytes
/// from distance o+1. L = 7 adds one extra length byte at level 1, or a 255-continued
/// sequence at level 2. Level 2 can also escape to a 16-bit far distance
/// (<c>o = 0x1FFF</c>, low byte 255, then two big-endian bytes plus 8191).
/// </para>
/// </remarks>
public static class FastLzCompressor {
  private const int MaxCopy = 32;
  private const int MaxLength = 264;
  private const int MaxL1Distance = 8192;
  private const int MaxL2Distance = 8191;
  private const int MaxFarDistance = 65535 + MaxL2Distance - 1;
  private const int HashLog = 13;
  private const int HashSize = 1 << HashLog;

  /// <summary>
  /// Compresses <paramref name="data"/> the way <c>fastlz_compress</c> does: level 1 below
  /// 64 KiB, level 2 from there on.
  /// </summary>
  /// <param name="data">The uncompressed input bytes.</param>
  /// <returns>The FastLZ block; empty for empty input.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) => Compress(data, data.Length < 65536 ? 1 : 2);

  /// <summary>Compresses <paramref name="data"/> at FastLZ level <paramref name="level"/> (1 or 2).</summary>
  /// <param name="data">The uncompressed input bytes.</param>
  /// <param name="level">1 for the fastest coding, 2 for longer matches and far distances.</param>
  /// <returns>The FastLZ block; empty for empty input.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is neither 1 nor 2.</exception>
  public static byte[] Compress(ReadOnlySpan<byte> data, int level) {
    if (level is not (1 or 2))
      throw new ArgumentOutOfRangeException(nameof(level), level, "FastLZ has levels 1 and 2.");
    if (data.IsEmpty)
      return [];

    // Literal runs cost one byte per 32; matches never cost more than they cover.
    var output = new byte[data.Length + data.Length / MaxCopy + 16];
    var op = 0;
    var length = data.Length;
    var ipBound = length - 4;
    var ipLimit = length - 12 - 1;
    var maxDistance = level == 1 ? MaxL1Distance : MaxFarDistance;
    var table = new int[HashSize];
    var anchor = 0;
    var ip = 2;

    while (ip < ipLimit) {
      int reference;
      int distance;
      uint sequence;
      uint candidate;
      do {
        sequence = ReadU32(data, ip) & 0xFFFFFF;
        var hash = Hash(sequence);
        reference = table[hash];
        table[hash] = ip;
        distance = ip - reference;
        candidate = distance < maxDistance ? ReadU32(data, reference) & 0xFFFFFF : 0x1000000u;
        if (ip >= ipLimit)
          break;
        ++ip;
      } while (sequence != candidate);

      if (ip >= ipLimit)
        break;
      --ip;

      // A far match must be at least five bytes long to pay for its distance.
      if (level == 2 && distance >= MaxL2Distance && (data[reference + 3] != data[ip + 3] || data[reference + 4] != data[ip + 4])) {
        ++ip;
        continue;
      }

      if (ip > anchor)
        op = Literals(ip - anchor, data, anchor, output, op);

      var matched = Compare(data, reference + 3, ip + 3, ipBound);
      op = level == 1 ? Match1(matched, distance, output, op) : Match2(matched, distance, output, op);

      ip += matched;
      sequence = ReadU32(data, ip);
      table[Hash(sequence & 0xFFFFFF)] = ip++;
      sequence >>= 8;
      table[Hash(sequence)] = ip++;
      anchor = ip;
    }

    op = Literals(length - anchor, data, anchor, output, op);
    if (level == 2)
      output[0] |= 1 << 5;
    return output[..op];
  }

  private static uint ReadU32(ReadOnlySpan<byte> data, int at) {
    // fastlz.c reads whole words; near the end the missing bytes are zero here.
    if (at + 4 <= data.Length)
      return BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
    uint value = 0;
    for (var i = 0; i < 4 && at + i < data.Length; ++i)
      value |= (uint)data[at + i] << (8 * i);
    return value;
  }

  private static int Hash(uint value) => (int)((value * 2654435769u) >> (32 - HashLog)) & (HashSize - 1);

  // flz_cmp: counts the matching bytes from p and q until r, plus the mismatching byte.
  private static int Compare(ReadOnlySpan<byte> data, int p, int q, int r) {
    var start = p;
    if (q + 4 <= data.Length && p + 4 <= data.Length && ReadU32(data, p) == ReadU32(data, q)) {
      p += 4;
      q += 4;
    }

    while (q < r)
      if (data[p++] != data[q++])
        break;
    return p - start;
  }

  private static int Literals(int runs, ReadOnlySpan<byte> data, int source, byte[] output, int op) {
    while (runs >= MaxCopy) {
      output[op++] = MaxCopy - 1;
      data.Slice(source, MaxCopy).CopyTo(output.AsSpan(op));
      source += MaxCopy;
      op += MaxCopy;
      runs -= MaxCopy;
    }

    if (runs > 0) {
      output[op++] = (byte)(runs - 1);
      data.Slice(source, runs).CopyTo(output.AsSpan(op));
      op += runs;
    }

    return op;
  }

  private static int Match1(int length, int distance, byte[] output, int op) {
    --distance;
    while (length > MaxLength - 2) {
      output[op++] = (byte)((7 << 5) + (distance >> 8));
      output[op++] = MaxLength - 2 - 7 - 2;
      output[op++] = (byte)distance;
      length -= MaxLength - 2;
    }

    if (length < 7) {
      output[op++] = (byte)((length << 5) + (distance >> 8));
      output[op++] = (byte)distance;
    } else {
      output[op++] = (byte)((7 << 5) + (distance >> 8));
      output[op++] = (byte)(length - 7);
      output[op++] = (byte)distance;
    }

    return op;
  }

  private static int Match2(int length, int distance, byte[] output, int op) {
    --distance;
    if (distance < MaxL2Distance) {
      if (length < 7) {
        output[op++] = (byte)((length << 5) + (distance >> 8));
        output[op++] = (byte)distance;
      } else {
        output[op++] = (byte)((7 << 5) + (distance >> 8));
        for (length -= 7; length >= 255; length -= 255)
          output[op++] = 255;
        output[op++] = (byte)length;
        output[op++] = (byte)distance;
      }

      return op;
    }

    distance -= MaxL2Distance;
    if (length < 7)
      output[op++] = (byte)((length << 5) + 31);
    else {
      output[op++] = (7 << 5) + 31;
      for (length -= 7; length >= 255; length -= 255)
        output[op++] = 255;
      output[op++] = (byte)length;
    }

    output[op++] = 255;
    output[op++] = (byte)(distance >> 8);
    output[op++] = (byte)distance;
    return op;
  }
}
