namespace Compression.Core.Entropy.Ppmd;

/// <summary>
/// Range encoder for PPMd, supporting multi-symbol frequency-based coding.
/// Unlike the LZMA range coder which uses adaptive binary probabilities, this
/// encoder works with cumulative frequency tables for multi-symbol alphabets.
/// </summary>
public sealed class PpmdRangeEncoder {
  private const uint Top = 1u << 24;
  private readonly Stream _output;
  private ulong _low;
  private uint _range;
  private int _cacheSize;
  private byte _cache;

  /// <summary>
  /// Initializes a new <see cref="PpmdRangeEncoder"/> writing to the specified stream.
  /// </summary>
  /// <param name="output">The output stream.</param>
  public PpmdRangeEncoder(Stream output) {
    this._output = output ?? throw new ArgumentNullException(nameof(output));
    this._range = 0xFFFFFFFF;
    this._cacheSize = 1;
  }

  /// <summary>
  /// Encodes a symbol with the given cumulative frequency range.
  /// </summary>
  /// <param name="lowCumFreq">Cumulative frequency of all symbols before this one.</param>
  /// <param name="freq">Frequency of the symbol being encoded.</param>
  /// <param name="totalFreq">Total of all symbol frequencies.</param>
  public void Encode(uint lowCumFreq, uint freq, uint totalFreq) {
    var r = this._range / totalFreq;
    this._low += (ulong)r * lowCumFreq;
    this._range = r * freq;
    this.Normalize();
  }

  /// <summary>
  /// Flushes remaining state to the output, completing the encoding.
  /// </summary>
  public void Finish() {
    for (var i = 0; i < 5; ++i)
      this.ShiftLow();
  }

  private void Normalize() {
    while (this._range < PpmdRangeEncoder.Top) {
      this._range <<= 8;
      this.ShiftLow();
    }
  }

  private void ShiftLow() {
    if ((uint)this._low < 0xFF000000u || (this._low >> 32) != 0) {
      var temp = this._cache;
      do {
        this._output.WriteByte((byte)(temp + (byte)(this._low >> 32)));
        temp = 0xFF;
      }
      while (--this._cacheSize > 0);

      this._cache = (byte)((uint)this._low >> 24);
    }

    ++this._cacheSize;
    this._low = (uint)(this._low << 8);
  }
}

/// <summary>
/// Range decoder for PPMd, supporting multi-symbol frequency-based coding.
/// Unlike the LZMA range coder which uses adaptive binary probabilities, this
/// decoder works with cumulative frequency tables for multi-symbol alphabets.
/// </summary>
public sealed class PpmdRangeDecoder {
  private const uint Top = 1u << 24;
  private readonly Stream _input;
  private uint _code;
  private uint _range;

  /// <summary>
  /// Initializes a new <see cref="PpmdRangeDecoder"/> reading from the specified stream.
  /// </summary>
  /// <param name="input">The input stream.</param>
  public PpmdRangeDecoder(Stream input) {
    this._input = input ?? throw new ArgumentNullException(nameof(input));
    this._range = 0xFFFFFFFF;

    // Read initial 5 bytes: first byte is the leading byte from the cache scheme,
    // next 4 bytes form the initial code value (same protocol as the LZMA range coder)
    var leadByte = this._input.ReadByte();
    if (leadByte < 0)
      leadByte = 0;

    for (var i = 0; i < 4; ++i) {
      var b = this._input.ReadByte();
      if (b < 0)
        b = 0;

      this._code = (this._code << 8) | (uint)b;
    }
  }

  /// <summary>
  /// Gets whether the input stream is exhausted.
  /// </summary>
  public bool IsFinished { get; private set; }

  /// <summary>
  /// Gets the current cumulative frequency threshold for symbol lookup.
  /// The caller uses this value to determine which symbol falls in this range,
  /// then calls <see cref="Decode"/> with the symbol's frequency information.
  /// </summary>
  /// <param name="totalFreq">Total of all symbol frequencies.</param>
  /// <returns>A value in [0, totalFreq) indicating where the encoded symbol falls.</returns>
  public uint GetThreshold(uint totalFreq) {
    this._range /= totalFreq;
    return this._code / this._range;
  }

  /// <summary>
  /// Updates the decoder state after the caller has identified the decoded symbol.
  /// Must be called after <see cref="GetThreshold"/> once the symbol is determined.
  /// </summary>
  /// <param name="lowCumFreq">Cumulative frequency of all symbols before the decoded one.</param>
  /// <param name="freq">Frequency of the decoded symbol.</param>
  /// <param name="totalFreq">Total of all symbol frequencies (same value passed to GetThreshold).</param>
  public void Decode(uint lowCumFreq, uint freq, uint totalFreq) {
    // Note: _range was already divided by totalFreq in GetThreshold
    this._code -= this._range * lowCumFreq;
    this._range *= freq;
    this.Normalize();
  }

  private void Normalize() {
    while (this._range < PpmdRangeDecoder.Top) {
      this._range <<= 8;
      var b = this._input.ReadByte();
      if (b < 0) {
        this.IsFinished = true;
        b = 0;
      }

      this._code = (this._code << 8) | (uint)b;
    }
  }
}
