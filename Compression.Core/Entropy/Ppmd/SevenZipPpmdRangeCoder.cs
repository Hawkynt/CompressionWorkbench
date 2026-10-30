namespace Compression.Core.Entropy.Ppmd;

/// <summary>
/// The range decoder 7-Zip pairs with PPMd variant H (7z coder 03 04 01): LZMA-style, 32-bit
/// range, a leading zero byte, normalisation below 2^24.
/// </summary>
/// <remarks>Converted from Ppmd7z_RangeDec in Igor Pavlov's public-domain Ppmd7Dec.c (7-Zip 9.20).</remarks>
public sealed class SevenZipPpmdRangeDecoder : IPpmd7RangeDecoder {
  private const uint Top = 1u << 24;
  private readonly byte[] _input;
  private int _position;
  private uint _range = 0xFFFFFFFF;
  private uint _code;

  /// <summary>Starts decoding <paramref name="input"/> at <paramref name="offset"/>.</summary>
  public SevenZipPpmdRangeDecoder(byte[] input, int offset = 0) {
    this._input = input;
    this._position = offset;
    var lead = this.NextByte();
    for (var i = 0; i < 4; ++i)
      this._code = (this._code << 8) | this.NextByte();

    this.IsValidStart = lead == 0 && this._code < 0xFFFFFFFF;
  }

  /// <summary>False when the stream cannot have come from the encoder (non-zero first byte).</summary>
  public bool IsValidStart { get; }

  /// <summary>Bytes read past the end of the input (each read as zero).</summary>
  public int Overrun { get; private set; }

  /// <summary>True once the decoder stands exactly where the encoder's flush ended.</summary>
  public bool FinishedOk => this._code == 0;

  /// <inheritdoc />
  public uint GetThreshold(uint total) => this._code / (this._range /= total);

  /// <inheritdoc />
  public void Decode(uint start, uint size) {
    this._code -= start * this._range;
    this._range *= size;
    this.Normalize();
  }

  /// <inheritdoc />
  public uint DecodeBit(uint size0) {
    var bound = (this._range >> 14) * size0;
    uint bit;
    if (this._code < bound) {
      bit = 0;
      this._range = bound;
    } else {
      bit = 1;
      this._code -= bound;
      this._range -= bound;
    }

    this.Normalize();
    return bit;
  }

  private void Normalize() {
    while (this._range < Top) {
      this._code = (this._code << 8) | this.NextByte();
      this._range <<= 8;
    }
  }

  private uint NextByte() {
    if (this._position < this._input.Length)
      return this._input[this._position++];

    ++this.Overrun;
    return 0;
  }
}

/// <summary>The range encoder matching <see cref="SevenZipPpmdRangeDecoder"/>.</summary>
/// <remarks>Converted from Ppmd7z_RangeEnc in Igor Pavlov's public-domain Ppmd7Enc.c (7-Zip 9.20).</remarks>
public sealed class SevenZipPpmdRangeEncoder(Stream output) : IPpmd7RangeEncoder {
  private const uint Top = 1u << 24;
  private ulong _low;
  private uint _range = 0xFFFFFFFF;
  private byte _cache;
  private long _cacheSize = 1;

  /// <inheritdoc />
  public void Encode(uint start, uint size, uint total) {
    this._low += start * (this._range /= total);
    this._range *= size;
    this.Normalize();
  }

  /// <inheritdoc />
  public void EncodeBit(uint size0, uint bit) {
    var bound = (this._range >> 14) * size0;
    if (bit == 0)
      this._range = bound;
    else {
      this._low += bound;
      this._range -= bound;
    }

    this.Normalize();
  }

  /// <summary>Writes the final five bytes.</summary>
  public void Flush() {
    for (var i = 0; i < 5; ++i)
      this.ShiftLow();
  }

  private void Normalize() {
    while (this._range < Top) {
      this._range <<= 8;
      this.ShiftLow();
    }
  }

  private void ShiftLow() {
    if ((uint)this._low < 0xFF000000u || (this._low >> 32) != 0) {
      var carry = (byte)(this._low >> 32);
      var temp = this._cache;
      do {
        output.WriteByte((byte)(temp + carry));
        temp = 0xFF;
      } while (--this._cacheSize != 0);

      this._cache = (byte)((uint)this._low >> 24);
    }

    ++this._cacheSize;
    this._low = (uint)this._low << 8;
  }
}
