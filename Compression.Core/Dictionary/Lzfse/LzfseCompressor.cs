using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Lzfse;

/// <summary>
/// Compresses data using the LZFSE-inspired block format (see <see cref="LzfseConstants"/>).
/// </summary>
public static class LzfseCompressor {
  /// <summary>
  /// Compresses the input data.
  /// </summary>
  /// <param name="source">The data to compress.</param>
  /// <returns>The compressed block.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> source) {
    var output = new List<byte>(source.Length / 2 + 32);
    LzfseValueStream.WriteInt(output, source.Length);

    var data = source.ToArray();
    var finder = new HashChainMatchFinder(Math.Max(data.Length, 1));

    var literalLengths = new List<int>();
    var matchLengths = new List<int>();
    var distances = new List<int>();
    var literalBytes = new List<byte>(data.Length);

    var pos = 0;
    var literalStart = 0;

    while (pos < data.Length) {
      if (pos + LzfseConstants.MinMatch <= data.Length) {
        var match = finder.FindMatch(data, pos, data.Length, data.Length - pos, LzfseConstants.MinMatch);
        if (match.Length >= LzfseConstants.MinMatch) {
          var literalRun = pos - literalStart;
          literalLengths.Add(literalRun);
          matchLengths.Add(match.Length - LzfseConstants.MinMatch);
          distances.Add(match.Distance);
          for (var i = 0; i < literalRun; ++i)
            literalBytes.Add(data[literalStart + i]);

          for (var i = 1; i < match.Length; ++i)
            finder.InsertPosition(data, pos + i);

          pos += match.Length;
          literalStart = pos;
          continue;
        }
      }

      ++pos;
    }

    var trailingLiteralRun = pos - literalStart;
    literalLengths.Add(trailingLiteralRun);
    for (var i = 0; i < trailingLiteralRun; ++i)
      literalBytes.Add(data[literalStart + i]);

    LzfseValueStream.WriteInt(output, matchLengths.Count);
    LzfseValueStream.WriteInt(output, literalBytes.Count);
    LzfseValueStream.WriteValues(output, literalLengths);
    LzfseValueStream.WriteValues(output, matchLengths);
    LzfseValueStream.WriteValues(output, distances);
    LzfseValueStream.WriteBlock(output, FseByteCodec.Encode(literalBytes.ToArray()));

    return output.ToArray();
  }
}
