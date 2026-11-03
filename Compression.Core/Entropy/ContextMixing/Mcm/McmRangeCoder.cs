namespace Compression.Core.Entropy.ContextMixing.Mcm;

/// <summary>
/// The carry-propagating range encoder mcm uses (7-Zip's design, 12-bit probabilities of a one bit).
/// See <c>docs/MCM-ON-DISK.md</c>, "Range coder".
/// </summary>
internal sealed class McmRangeEncoder {
  private const uint TopValue = 1u << 24;

  private ulong _low;
  private uint _range = 0xFFFFFFFF;
  private byte _cache;
  private long _cacheSize = 1;
  private byte[] _buffer;
  private int _length;

  public McmRangeEncoder(int capacityHint = 4096) => this._buffer = new byte[Math.Max(16, capacityHint)];

  /// <summary>Bytes written so far.</summary>
  public int Length => this._length;

  /// <summary>Codes <paramref name="bit"/> with probability <paramref name="probability"/>/4096 of it being one.</summary>
  public void Encode(int bit, int probability) {
    var mid = (this._range >> 12) * (uint)probability;
    if (bit != 0)
      this._range = mid;
    else {
      this._low += mid;
      this._range -= mid;
    }
    while (this._range < TopValue) {
      this._range <<= 8;
      this.ShiftLow();
    }
  }

  /// <summary>Codes a bit with probability one half (Wav16's direct bits).</summary>
  public void EncodeDirect(int bit) {
    this._range >>= 1;
    if (bit != 0)
      this._low += this._range;
    while (this._range < TopValue) {
      this._range <<= 8;
      this.ShiftLow();
    }
  }

  /// <summary>Flushes the coder and returns everything written.</summary>
  public byte[] Finish() {
    for (var i = 0; i < 5; ++i)
      this.ShiftLow();
    return this._buffer.AsSpan(0, this._length).ToArray();
  }

  private void ShiftLow() {
    if ((uint)this._low < 0xFF000000u || (this._low >> 32) != 0) {
      var carry = (byte)(this._low >> 32);
      var temp = this._cache;
      do {
        this.Put((byte)(temp + carry));
        temp = 0xFF;
      } while (--this._cacheSize != 0);
      this._cache = (byte)(this._low >> 24);
    }
    ++this._cacheSize;
    this._low = (uint)(this._low << 8);
  }

  private void Put(byte value) {
    if (this._length == this._buffer.Length)
      Array.Resize(ref this._buffer, this._buffer.Length * 2);
    this._buffer[this._length++] = value;
  }
}

/// <summary>The decoder matching <see cref="McmRangeEncoder"/>; bytes past the input's end read as 0xFF.</summary>
internal sealed class McmRangeDecoder {
  private const uint TopValue = 1u << 24;

  private readonly byte[] _input;
  private readonly int _end;
  private int _position;
  private uint _range = 0xFFFFFFFF;
  private uint _code;

  public McmRangeDecoder(byte[] input, int offset, int end) {
    this._input = input;
    this._position = offset;
    this._end = end;
    for (var i = 0; i < 5; ++i)
      this._code = (this._code << 8) | this.Next();
  }

  /// <summary>Position just past the last byte the decoder consumed — where the coded stream ends.</summary>
  public int Position => this._position;

  /// <summary>Whether the decoder had to read past the end of its input.</summary>
  public bool Overran => this._position > this._end;

  /// <summary>Decodes a bit coded with probability <paramref name="probability"/>/4096 of a one.</summary>
  public int Decode(int probability) {
    var mid = (this._range >> 12) * (uint)probability;
    int bit;
    if (this._code < mid) {
      this._range = mid;
      bit = 1;
    } else {
      this._code -= mid;
      this._range -= mid;
      bit = 0;
    }
    while (this._range < TopValue) {
      this._code = (this._code << 8) | this.Next();
      this._range <<= 8;
    }
    return bit;
  }

  /// <summary>Decodes a bit coded with <see cref="McmRangeEncoder.EncodeDirect"/>.</summary>
  public int DecodeDirect() {
    this._range >>= 1;
    int bit;
    if (this._code >= this._range) {
      this._code -= this._range;
      bit = 1;
    } else
      bit = 0;
    while (this._range < TopValue) {
      this._code = (this._code << 8) | this.Next();
      this._range <<= 8;
    }
    return bit;
  }

  private uint Next() {
    var position = this._position++;
    return position < this._end ? this._input[position] : 0xFFu;
  }
}
