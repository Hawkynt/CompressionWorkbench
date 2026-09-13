namespace FileFormat.Zstd;

/// <summary>
/// Reads a Zstandard FSE table description (RFC 8878, section 4.1.1) — the bit-packed
/// normalized-count representation used for the literal-length, offset, and match-length
/// distributions of compressed blocks produced by the reference <c>zstd</c> implementation.
/// </summary>
internal static class ZstdFseTableReader {
  /// <summary>
  /// Parses normalized counts from the FSE table description.
  /// </summary>
  /// <param name="data">The data starting at the Accuracy_Log nibble.</param>
  /// <returns>Normalized counts (value -1 means "less than 1"), max symbol, table log, and bytes consumed.</returns>
  public static (short[] NormalizedCounts, int MaxSymbol, int TableLog, int BytesRead)
    Read(ReadOnlySpan<byte> data) {
    if (data.Length < 1)
      throw new InvalidDataException("Truncated FSE table description.");

    var br = new ForwardBitReader(data);

    var accuracyLog = br.ReadBits(4) + 5;
    if (accuracyLog is < 5 or > 9)
      throw new InvalidDataException($"Invalid FSE accuracy log: {accuracyLog}.");

    var tableSize = 1 << accuracyLog;
    var counts = new short[256];
    var remaining = tableSize + 1;
    var symbol = 0;
    var maxSymbol = 0;

    while (remaining > 1 && symbol < 256) {
      var maxValue = remaining; // inclusive
      var nbBits = BitWidth(maxValue);
      var threshold = (1 << nbBits) - 1 - maxValue;

      var value = br.ReadBits(nbBits - 1);
      if (value >= threshold) {
        var extra = br.ReadBits(1);
        value += extra << (nbBits - 1);
        if (value >= (1 << (nbBits - 1)))
          value -= threshold;
      }

      var proba = value - 1; // -1 means "less than 1"
      counts[symbol] = (short)proba;
      if (proba != 0)
        maxSymbol = symbol;
      remaining -= proba < 0 ? 1 : proba;

      if (proba == 0) {
        while (true) {
          var repeat = br.ReadBits(2);
          symbol += repeat;
          if (repeat != 3)
            break;
        }
      }

      ++symbol;
    }

    if (remaining != 1)
      throw new InvalidDataException("FSE normalized counts do not sum to the table size.");

    var bytesRead = br.BytesConsumed;
    var normalized = new short[maxSymbol + 1];
    Array.Copy(counts, normalized, maxSymbol + 1);
    return (normalized, maxSymbol, accuracyLog, bytesRead);
  }

  private static int BitWidth(int value) {
    var bits = 1;
    while ((1 << bits) <= value)
      ++bits;
    return bits;
  }

  /// <summary>Reads bits LSB-first from the front of a byte span.</summary>
  private ref struct ForwardBitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bitPos = 0;

    public int BytesConsumed => (this._bitPos + 7) >> 3;

    public int ReadBits(int n) {
      var value = 0;
      for (var i = 0; i < n; ++i) {
        var byteIdx = this._bitPos >> 3;
        var bitIdx = this._bitPos & 7;
        var bit = byteIdx < this._data.Length ? (this._data[byteIdx] >> bitIdx) & 1 : 0;
        value |= bit << i;
        ++this._bitPos;
      }

      return value;
    }
  }
}
