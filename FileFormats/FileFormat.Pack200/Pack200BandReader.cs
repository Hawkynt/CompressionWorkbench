namespace FileFormat.Pack200;

/// <summary>
/// A forward-only cursor over the byte stream of a Pack200 segment that decodes
/// bands of integers using their BHSD codings (JSR-200 section 5.4).
/// </summary>
public sealed class Pack200BandReader {
  private readonly byte[] _data;
  private int _pos;

  /// <summary>Creates a reader positioned at the start of <paramref name="data"/>.</summary>
  public Pack200BandReader(byte[] data) {
    this._data = data ?? throw new ArgumentNullException(nameof(data));
  }

  /// <summary>Current byte offset of the cursor.</summary>
  public int Position => this._pos;

  /// <summary>Total number of bytes available.</summary>
  public int Length => this._data.Length;

  /// <summary>Reads a single raw byte, advancing the cursor.</summary>
  public int ReadByte() {
    if (this._pos >= this._data.Length)
      throw new InvalidDataException("Pack200: unexpected end of band data.");
    return this._data[this._pos++];
  }

  /// <summary>
  /// Decodes one value using <paramref name="coding"/>. The delta flag is intentionally
  /// ignored here; callers use <see cref="ReadBand"/> to apply running deltas across a band.
  /// </summary>
  public long ReadValue(Pack200Coding coding) {
    var l = coding.L;
    long value = 0;
    long scale = 1;
    for (var i = 0; i < coding.B; ++i) {
      var b = this.ReadByte();
      value += b * scale;
      if (b < l)
        break;
      scale *= coding.H;
    }
    return ApplySign(value, coding.S);
  }

  /// <summary>
  /// Reads <paramref name="count"/> values as a single band, applying the coding's
  /// signedness per value and its running-delta accumulation across the band.
  /// </summary>
  public long[] ReadBand(Pack200Coding coding, int count) {
    if (count < 0)
      throw new ArgumentOutOfRangeException(nameof(count));
    var result = new long[count];
    long running = 0;
    for (var i = 0; i < count; ++i) {
      var v = this.ReadValue(coding);
      if (coding.D != 0) {
        running += v;
        result[i] = running;
      } else {
        result[i] = v;
      }
    }
    return result;
  }

  /// <summary>
  /// Folds an unsigned magnitude into a signed value using the S-bit sign convention
  /// from JSR-200 section 5.4.3. S=0 leaves the value unsigned.
  /// </summary>
  private static long ApplySign(long value, int s) {
    if (s == 0)
      return value;
    var mask = (1L << s) - 1;
    // The low S bits carry the sign: all-ones => negative, otherwise non-negative.
    return (value & mask) == mask
      ? -(value >> s) - 1
      : value - (value >> s);
  }
}
