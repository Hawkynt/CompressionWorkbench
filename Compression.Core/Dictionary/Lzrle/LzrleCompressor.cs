using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Lzrle;

/// <summary>
/// Compresses data using the LZRLE block format (see <see cref="LzrleConstants"/>).
/// </summary>
public static class LzrleCompressor {
  /// <summary>
  /// Compresses the input data using LZRLE.
  /// </summary>
  /// <param name="source">The data to compress.</param>
  /// <returns>The compressed data, prefixed with a 4-byte little-endian original length.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> source) {
    var output = new List<byte>(source.Length / 2 + 16);
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, source.Length);
    output.AddRange(header);

    if (source.Length == 0)
      return output.ToArray();

    var data = source.ToArray();
    var finder = new HashChainMatchFinder(Math.Max(data.Length, 1));

    var pos = 0;
    var literalStart = 0;
    var distanceBytes = new byte[4];

    while (pos < data.Length) {
      // 1. Repeated-byte run detection (cheapest to encode when it applies).
      var runValue = data[pos];
      var runLen = 1;
      while (pos + runLen < data.Length && data[pos + runLen] == runValue)
        ++runLen;

      if (runLen >= LzrleConstants.MinRun) {
        FlushLiterals(output, data, literalStart, pos - literalStart);
        WriteToken(output, LzrleConstants.TypeRun, runLen, LzrleConstants.MinRun);
        output.Add(runValue);
        for (var i = 1; i < runLen; ++i)
          finder.InsertPosition(data, pos + i);
        pos += runLen;
        literalStart = pos;
        continue;
      }

      // 2. Dictionary match search.
      if (pos + LzrleConstants.MinMatch <= data.Length) {
        var match = finder.FindMatch(data, pos, data.Length, data.Length - pos, LzrleConstants.MinMatch);
        if (match.Length >= LzrleConstants.MinMatch) {
          FlushLiterals(output, data, literalStart, pos - literalStart);
          WriteToken(output, LzrleConstants.TypeMatch, match.Length, LzrleConstants.MinMatch);
          BinaryPrimitives.WriteUInt32LittleEndian(distanceBytes, (uint)match.Distance);
          output.AddRange(distanceBytes);
          for (var i = 1; i < match.Length; ++i)
            finder.InsertPosition(data, pos + i);
          pos += match.Length;
          literalStart = pos;
          continue;
        }
      }

      // 3. No run or match: accumulate as a literal.
      ++pos;
    }

    FlushLiterals(output, data, literalStart, pos - literalStart);
    return output.ToArray();
  }

  private static void FlushLiterals(List<byte> output, byte[] data, int start, int count) {
    if (count == 0)
      return;

    WriteToken(output, LzrleConstants.TypeLiteral, count, 0);
    for (var i = 0; i < count; ++i)
      output.Add(data[start + i]);
  }

  private static void WriteToken(List<byte> output, int type, int length, int baseValue) {
    var field = length - baseValue;
    if (field < LzrleConstants.LengthFieldMax) {
      output.Add((byte)((type << LzrleConstants.LengthFieldBits) | field));
      return;
    }

    output.Add((byte)((type << LzrleConstants.LengthFieldBits) | LzrleConstants.LengthFieldMax));
    var remainder = field - LzrleConstants.LengthFieldMax;
    while (remainder >= 255) {
      output.Add(255);
      remainder -= 255;
    }
    output.Add((byte)remainder);
  }
}
