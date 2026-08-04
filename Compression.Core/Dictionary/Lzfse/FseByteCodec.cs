using System.Numerics;
using Compression.Core.Entropy.Fse;

namespace Compression.Core.Dictionary.Lzfse;

/// <summary>
/// Wraps the project's shared FSE (tANS) encoder/decoder to compress and decompress
/// a plain byte-symbol stream, self-describing via a small header of normalized
/// counts. Used for LZFSE's literal byte stream and its bucketed literal-length,
/// match-length and distance symbol streams alike.
/// </summary>
internal static class FseByteCodec {
  /// <summary>
  /// Compresses a byte-symbol stream with FSE.
  /// </summary>
  /// <param name="symbols">The symbols to compress.</param>
  /// <returns>The header (normalized counts) followed by the FSE-coded bitstream, or an empty array for empty input.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> symbols) {
    if (symbols.Length == 0)
      return [];

    var counts = new int[256];
    foreach (var b in symbols)
      ++counts[b];

    var maxSymbol = 0;
    for (var s = 255; s >= 0; --s)
      if (counts[s] > 0) {
        maxSymbol = s;
        break;
      }

    var distinct = 0;
    for (var s = 0; s <= maxSymbol; ++s)
      if (counts[s] > 0)
        ++distinct;

    var tableLog = ChooseTableLog(distinct, symbols.Length);
    var normalized = FseNormalizer.Normalize(counts, maxSymbol, tableLog);

    var header = new byte[3 + (maxSymbol + 1) * 2];
    FseEncoder.WriteNormalizedCounts(header, 0, normalized, maxSymbol, tableLog);

    var encoder = new FseEncoder(normalized, maxSymbol, tableLog);
    var body = encoder.Encode(symbols);

    var result = new byte[header.Length + body.Length];
    header.CopyTo(result, 0);
    body.CopyTo(result, header.Length);
    return result;
  }

  /// <summary>
  /// Decompresses a byte-symbol stream produced by <see cref="Encode"/>.
  /// </summary>
  /// <param name="data">The header and FSE-coded bitstream.</param>
  /// <param name="symbolCount">The number of symbols originally encoded.</param>
  /// <returns>The decompressed symbols.</returns>
  public static byte[] Decode(ReadOnlySpan<byte> data, int symbolCount) {
    if (symbolCount == 0)
      return [];

    var (normalized, maxSymbol, tableLog, bytesRead) = FseDecoder.ReadNormalizedCounts(data);
    var decoder = new FseDecoder(normalized, maxSymbol, tableLog);
    return decoder.Decode(data[bytesRead..], symbolCount);
  }

  private static int ChooseTableLog(int distinctSymbols, int dataLength) {
    var log = FseConstants.MinTableLog;
    while ((1 << log) < distinctSymbols && log < FseConstants.MaxTableLog)
      ++log;

    var bitLength = dataLength <= 1 ? 1 : 32 - BitOperations.LeadingZeroCount((uint)dataLength);
    log = Math.Max(log, Math.Min(bitLength, FseConstants.DefaultTableLog));

    return Math.Clamp(log, FseConstants.MinTableLog, FseConstants.MaxTableLog);
  }
}
