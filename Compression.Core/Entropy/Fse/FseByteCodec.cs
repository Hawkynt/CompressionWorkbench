using System.Buffers.Binary;

namespace Compression.Core.Entropy.Fse;

/// <summary>
/// Self-describing byte-oriented wrapper around the reusable FSE/tANS state coder.
/// </summary>
/// <remarks>
/// The wrapper is a CompressionWorkbench building-block format rather than a standardized
/// interchange format. It stores the uncompressed length and normalized-count table in
/// front of the FSE payload so the decoder requires no out-of-band probability model.
/// </remarks>
public static class FseByteCodec {
  /// <summary>The default FSE table log used by the FSE building block.</summary>
  public const int DefaultTableLog = 10;

  /// <summary>
  /// Compresses a byte sequence using a normalized FSE/tANS table.
  /// </summary>
  /// <param name="data">Input bytes.</param>
  /// <param name="tableLog">Log2 of the FSE state count.</param>
  public static byte[] Compress(ReadOnlySpan<byte> data, int tableLog = DefaultTableLog) {
    if (tableLog is < FseConstants.MinTableLog or > FseConstants.MaxTableLog)
      throw new ArgumentOutOfRangeException(nameof(tableLog), tableLog,
        $"FSE table log must be in [{FseConstants.MinTableLog}, {FseConstants.MaxTableLog}].");

    if (data.Length == 0) {
      var empty = new byte[4];
      BinaryPrimitives.WriteInt32LittleEndian(empty, 0);
      return empty;
    }

    Span<int> counts = stackalloc int[256];
    var maxSymbol = 0;
    foreach (var value in data) {
      ++counts[value];
      if (value > maxSymbol)
        maxSymbol = value;
    }

    var normalized = FseEncoder.NormalizeCounts(counts.ToArray(), maxSymbol, tableLog);
    var encoder = new FseEncoder(normalized, maxSymbol, tableLog);
    var payload = encoder.Encode(data);

    var modelBytes = checked(3 + (maxSymbol + 1) * sizeof(short));
    var result = new byte[checked(4 + modelBytes + payload.Length)];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    var written = FseEncoder.WriteNormalizedCounts(result, 4, normalized, maxSymbol, tableLog);
    if (written != modelBytes)
      throw new InvalidOperationException("FSE normalized-count writer returned an unexpected size.");
    payload.CopyTo(result, 4 + written);
    return result;
  }

  /// <summary>
  /// Decompresses a sequence produced by <see cref="Compress(ReadOnlySpan{byte}, int)"/>.
  /// </summary>
  /// <exception cref="InvalidDataException">The self-describing stream is truncated or malformed.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      throw new InvalidDataException("FSE stream is shorter than its length field.");

    var originalLength = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalLength < 0)
      throw new InvalidDataException("FSE stream declares a negative uncompressed length.");
    if (originalLength == 0)
      return [];

    var model = data[4..];
    var (normalized, maxSymbol, tableLog, bytesRead) = FseDecoder.ReadNormalizedCounts(model);
    if (bytesRead >= model.Length)
      throw new InvalidDataException("FSE stream contains no entropy payload.");

    ValidateNormalizedCounts(normalized, tableLog);
    var decoder = new FseDecoder(normalized, maxSymbol, tableLog);
    return decoder.Decode(model[bytesRead..], originalLength);
  }

  private static void ValidateNormalizedCounts(ReadOnlySpan<short> counts, int tableLog) {
    var sum = 0;
    foreach (var count in counts) {
      if (count < -1)
        throw new InvalidDataException($"FSE normalized count {count} is invalid.");
      sum += count == -1 ? 1 : count;
    }

    var expected = 1 << tableLog;
    if (sum != expected)
      throw new InvalidDataException($"FSE normalized counts sum to {sum}, expected {expected}.");
  }
}
