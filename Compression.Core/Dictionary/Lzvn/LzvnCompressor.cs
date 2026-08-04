using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Lzvn;

/// <summary>
/// Compresses data using the LZVN block format (see <see cref="LzvnConstants"/>).
/// </summary>
public static class LzvnCompressor {
  /// <summary>
  /// Compresses the input data using LZVN.
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

    while (pos < data.Length) {
      if (pos + LzvnConstants.MinMatch <= data.Length) {
        var match = finder.FindMatch(data, pos, data.Length, data.Length - pos, LzvnConstants.MinMatch);
        if (match.Length >= LzvnConstants.MinMatch) {
          EmitToken(output, data, literalStart, pos - literalStart, match.Length, match.Distance);
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
      EmitFinalLiteralToken(output, data, literalStart, trailingLiteralCount);

    return output.ToArray();
  }

  private static void EmitToken(List<byte> output, byte[] data, int literalStart, int literalCount, int matchLength, int distance) {
    var literalField = literalCount < LzvnConstants.MaxDirectLiteral + 1 ? literalCount : LzvnConstants.LiteralExtended;
    var matchField = matchLength - LzvnConstants.MinMatch;
    var matchNibble = matchField <= LzvnConstants.MaxDirectMatch ? matchField : LzvnConstants.MatchExtended;

    output.Add((byte)((literalField << 4) | matchNibble));

    if (literalField == LzvnConstants.LiteralExtended)
      WriteExtended(output, literalCount - (LzvnConstants.MaxDirectLiteral + 1));

    for (var i = 0; i < literalCount; ++i)
      output.Add(data[literalStart + i]);

    if (matchNibble == LzvnConstants.MatchExtended)
      WriteExtended(output, matchField - LzvnConstants.MatchExtended);

    WriteDistance(output, distance);
  }

  private static void EmitFinalLiteralToken(List<byte> output, byte[] data, int literalStart, int literalCount) {
    var literalField = literalCount < LzvnConstants.MaxDirectLiteral + 1 ? literalCount : LzvnConstants.LiteralExtended;

    output.Add((byte)((literalField << 4) | LzvnConstants.MatchNone));

    if (literalField == LzvnConstants.LiteralExtended)
      WriteExtended(output, literalCount - (LzvnConstants.MaxDirectLiteral + 1));

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

  private static void WriteDistance(List<byte> output, int distance) {
    if (distance <= LzvnConstants.DistanceTier1Max) {
      output.Add((byte)(distance - 1));
      return;
    }

    if (distance <= LzvnConstants.DistanceTier2Max) {
      var rem = distance - (LzvnConstants.DistanceTier1Max + 1);
      var hi = rem >> 8;
      var lo = rem & 0xFF;
      output.Add((byte)(0x80 + hi));
      output.Add((byte)lo);
      return;
    }

    output.Add(LzvnConstants.DistanceTier3Marker);
    output.Add((byte)distance);
    output.Add((byte)(distance >> 8));
    output.Add((byte)(distance >> 16));
    output.Add((byte)(distance >> 24));
  }
}
