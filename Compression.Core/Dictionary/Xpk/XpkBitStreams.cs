namespace Compression.Core.Dictionary.Xpk;

/// <summary>
/// Shared input plumbing for the XPK sub-library decoders: a forward byte stream that can be
/// linked with a backward one consuming the same buffer from the far end, and MSB/LSB bit
/// readers that refill by whole words. Behaviour follows ancient's
/// <c>InputStream.hpp</c> (Teemu Suutari, BSD-2-Clause), see THIRD-PARTY-NOTICE.ancient.txt.
/// </summary>
internal sealed class XpkForwardStream(byte[] buffer, int start, int end) {
  private int _end = end;
  public int Offset { get; private set; } = start;
  public XpkBackwardStream? Linked { get; set; }

  public int End {
    get => this._end;
    set => this._end = value;
  }

  public byte ReadByte() {
    if (this.Offset >= this._end) throw new InvalidDataException("XPK: compressed data ends early.");
    var b = buffer[this.Offset++];
    if (this.Linked != null) this.Linked.End = this.Offset;
    return b;
  }

  public uint ReadBE16() => (uint)((this.ReadByte() << 8) | this.ReadByte());

  public uint ReadBE32() => (this.ReadBE16() << 16) | this.ReadBE16();
}

/// <summary>Reads bytes from the end of a buffer towards its start.</summary>
internal sealed class XpkBackwardStream(byte[] buffer, int start, int end) {
  private int _offset = end;
  public int End { get; set; } = start;
  public XpkForwardStream? Linked { get; set; }

  public byte ReadByte() {
    if (this._offset <= this.End) throw new InvalidDataException("XPK: compressed data ends early.");
    var b = buffer[--this._offset];
    if (this.Linked != null) this.Linked.End = this._offset;
    return b;
  }
}

/// <summary>Bit reader taking bits from the top of each refill word.</summary>
internal sealed class XpkMsbBitReader(Func<(uint Word, int Bits)> refill) {
  private uint _content;
  private int _length;

  public uint Read(int count) {
    uint result = 0;
    while (count > 0) {
      if (this._length == 0) (this._content, this._length) = refill();
      var n = Math.Min(count, this._length);
      this._length -= n;
      result = (result << n) | ((this._content >> this._length) & (uint)((1L << n) - 1));
      count -= n;
    }
    return result;
  }
}

/// <summary>Bit reader taking bits from the bottom of each refill word.</summary>
internal sealed class XpkLsbBitReader(Func<(uint Word, int Bits)> refill) {
  private uint _content;
  private int _length;

  public uint Read(int count) {
    uint result = 0;
    var pos = 0;
    while (count > 0) {
      if (this._length == 0) (this._content, this._length) = refill();
      var n = Math.Min(count, this._length);
      result |= (this._content & (uint)((1L << n) - 1)) << pos;
      this._content = n == 32 ? 0 : this._content >> n;
      this._length -= n;
      count -= n;
      pos += n;
    }
    return result;
  }
}

/// <summary>
/// ancient's VariableLengthCodeDecoder: entry <c>i</c> reads <c>bits[i]</c> raw bits on top
/// of an offset that accumulates <c>2^bits</c> per entry; a negative entry restarts the
/// accumulation at <c>2^|entry|</c> with an offset of zero.
/// </summary>
internal sealed class XpkVlc {
  private readonly int[] _bits;
  private readonly uint[] _offsets;

  public XpkVlc(params int[] lengths) {
    this._bits = new int[lengths.Length];
    this._offsets = new uint[lengths.Length];
    uint length = 0;
    for (var i = 0; i < lengths.Length; ++i) {
      if (lengths[i] < 0) {
        this._bits[i] = -lengths[i];
        this._offsets[i] = 0;
        length = 1u << -lengths[i];
        continue;
      }
      this._bits[i] = lengths[i];
      this._offsets[i] = length;
      length += 1u << lengths[i];
    }
  }

  public uint Decode(Func<int, uint> readBits, uint index) {
    if (index >= this._bits.Length) throw new InvalidDataException("XPK: variable-length code index out of range.");
    return this._offsets[index] + readBits(this._bits[index]);
  }

  public int Bits(int index) => this._bits[index];
  public uint Offset(int index) => this._offsets[index];
}
