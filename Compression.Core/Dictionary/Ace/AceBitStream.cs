// ACE bit stream, converted from acefile (https://github.com/droe/acefile, BSD-2-Clause,
// Copyright (c) 2017-2026 Daniel Roethlisberger and contributors). See THIRD-PARTY-NOTICE.acefile.txt.

using System.Buffers.Binary;

namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// ACE's bit stream: little-endian 32-bit words, each read most significant bit first. Up to 31
/// bits past the end of the data may be peeked and read as zeros, as the reference decoders do.
/// </summary>
internal sealed class AceBitStream {
  private readonly ReadOnlyMemory<byte> _data;
  private readonly long _totalBits;
  private long _pos;

  public AceBitStream(ReadOnlyMemory<byte> data) {
    if (data.Length % 4 != 0)
      throw new InvalidDataException("ACE compressed data is not a whole number of 32-bit words.");
    this._data = data;
    this._totalBits = (long)data.Length * 8;
  }

  private uint Word(long index) {
    var offset = index * 4;
    return offset + 4 <= this._data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(this._data.Span[(int)offset..]) : 0u;
  }

  /// <summary>Returns the next <paramref name="bits"/> (0..31) bits without consuming them.</summary>
  public uint PeekBits(int bits) {
    if (bits == 0)
      return 0;
    if (this._pos + bits > this._totalBits + 31)
      throw new EndOfStreamException("ACE bit stream exhausted.");
    var wordIndex = this._pos >> 5;
    var bitInWord = (int)(this._pos & 31);
    var value = ((ulong)this.Word(wordIndex) << 32) | this.Word(wordIndex + 1);
    return (uint)((value << bitInWord) >> (64 - bits));
  }

  public void SkipBits(int bits) {
    if (this._pos + bits > this._totalBits)
      throw new EndOfStreamException("ACE bit stream exhausted.");
    this._pos += bits;
  }

  public uint ReadBits(int bits) {
    var v = this.PeekBits(bits);
    this.SkipBits(bits);
    return v;
  }

  /// <summary>Reads a value of more than 31 bits by splitting it.</summary>
  public uint ReadBitsWide(int bits) => bits <= 24 ? this.ReadBits(bits) : (this.ReadBits(bits - 16) << 16) | this.ReadBits(16);

  /// <summary>Golomb-Rice code with <paramref name="rBits"/> remainder bits; signed values use the low bit as sign.</summary>
  public int ReadGolombRice(int rBits, bool signed = false) {
    var value = rBits == 0 ? 0 : (int)this.ReadBits(rBits);
    while (this.ReadBits(1) == 1)
      value += 1 << rBits;
    if (!signed)
      return value;
    return (value & 1) != 0 ? -(value >> 1) - 1 : value >> 1;
  }

  /// <summary>An unsigned integer of known bit width whose (always set) top bit is not stored.</summary>
  public uint ReadKnownWidthUInt(int bits) {
    if (bits < 2)
      return (uint)bits;
    --bits;
    return this.ReadBitsWide(bits) + (1u << bits);
  }
}

/// <summary>Writes ACE's bit stream (the inverse of <see cref="AceBitStream"/>), padded to whole words.</summary>
internal sealed class AceBitWriter {
  private readonly List<uint> _words = [];
  private uint _current;
  private int _used;

  public void WriteBits(uint value, int bits) {
    if (bits == 0)
      return;
    if (bits > 24) {
      this.WriteBits(value >> 16, bits - 16);
      this.WriteBits(value & 0xFFFF, 16);
      return;
    }
    value &= (1u << bits) - 1;
    var free = 32 - this._used;
    if (bits <= free) {
      this._current |= value << (free - bits);
      this._used += bits;
      if (this._used == 32)
        this.Flush();
      return;
    }
    var high = bits - free;
    this._current |= value >> high;
    this.Flush();
    this._current = value << (32 - high);
    this._used = high;
  }

  public void WriteKnownWidthUInt(uint value, int bits) {
    if (bits < 2)
      return;
    this.WriteBits(value & ((1u << (bits - 1)) - 1), bits - 1);
  }

  private void Flush() {
    this._words.Add(this._current);
    this._current = 0;
    this._used = 0;
  }

  public byte[] ToArray() {
    var words = new List<uint>(this._words);
    if (this._used > 0)
      words.Add(this._current);
    var result = new byte[words.Count * 4];
    for (var i = 0; i < words.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), words[i]);
    return result;
  }
}
