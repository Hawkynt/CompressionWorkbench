using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Lzturbo;

/// <summary>
/// Compresses data using the LZTURBO-inspired block format (see <see cref="LzturboConstants"/>).
/// </summary>
public static class LzturboCompressor {
  /// <summary>
  /// Compresses the input data.
  /// </summary>
  /// <param name="source">The data to compress.</param>
  /// <returns>The compressed block, including magic, method, and length header.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> source) {
    var body = new List<byte>(source.Length / 2 + 16);

    if (source.Length > 0) {
      var data = source.ToArray();
      var maxDistance = (1 << (LzturboConstants.DistanceBytes * 8)) - 1;
      var finder = new HashChainMatchFinder(Math.Max(data.Length, 1));

      var pos = 0;
      var literalStart = 0;

      while (pos < data.Length) {
        if (pos + LzturboConstants.MinMatch <= data.Length) {
          var match = finder.FindMatch(data, pos, Math.Min(maxDistance, data.Length), data.Length - pos, LzturboConstants.MinMatch);
          if (match.Length >= LzturboConstants.MinMatch) {
            EmitToken(body, data, literalStart, pos - literalStart, match.Length, match.Distance);
            for (var i = 1; i < match.Length; ++i)
              finder.InsertPosition(data, pos + i);
            pos += match.Length;
            literalStart = pos;
            continue;
          }
        }

        ++pos;
      }

      var trailingLiteralCount = pos - literalStart;
      if (trailingLiteralCount > 0)
        EmitFinalLiteralToken(body, data, literalStart, trailingLiteralCount);
    }

    var output = new List<byte>(LzturboConstants.HeaderSize + body.Count);
    output.AddRange(LzturboConstants.Magic);
    output.Add(LzturboConstants.Method);
    Span<byte> lengths = stackalloc byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(lengths, source.Length);
    BinaryPrimitives.WriteInt32LittleEndian(lengths[4..], body.Count);
    output.AddRange(lengths);
    output.AddRange(body);

    return output.ToArray();
  }

  private static void EmitToken(List<byte> output, byte[] data, int literalStart, int literalCount, int matchLength, int distance) {
    var literalField = literalCount < LzturboConstants.MaxDirectLiteral + 1 ? literalCount : LzturboConstants.LiteralExtended;
    var matchField = matchLength - LzturboConstants.MinMatch;
    var matchNibble = matchField <= LzturboConstants.MaxDirectMatch ? matchField : LzturboConstants.MatchExtended;

    output.Add((byte)((literalField << 4) | matchNibble));

    if (literalField == LzturboConstants.LiteralExtended)
      WriteExtended(output, literalCount - (LzturboConstants.MaxDirectLiteral + 1));

    for (var i = 0; i < literalCount; ++i)
      output.Add(data[literalStart + i]);

    if (matchNibble == LzturboConstants.MatchExtended)
      WriteExtended(output, matchField - LzturboConstants.MatchExtended);

    for (var i = 0; i < LzturboConstants.DistanceBytes; ++i)
      output.Add((byte)(distance >> (8 * i)));
  }

  private static void EmitFinalLiteralToken(List<byte> output, byte[] data, int literalStart, int literalCount) {
    var literalField = literalCount < LzturboConstants.MaxDirectLiteral + 1 ? literalCount : LzturboConstants.LiteralExtended;

    output.Add((byte)((literalField << 4) | LzturboConstants.MatchNone));

    if (literalField == LzturboConstants.LiteralExtended)
      WriteExtended(output, literalCount - (LzturboConstants.MaxDirectLiteral + 1));

    for (var i = 0; i < literalCount; ++i)
      output.Add(data[literalStart + i]);
  }

  private static void WriteExtended(List<byte> output, int remainder) {
    while (remainder >= 255) {
      output.Add(255);
      remainder -= 255;
    }
    output.Add((byte)remainder);
  }
}
