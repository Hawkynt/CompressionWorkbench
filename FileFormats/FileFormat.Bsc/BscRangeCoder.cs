// Ported from libbsc's range coder (Apache-2.0).
// Copyright (c) 2009-2025 Ilya Grebnov <ilya.grebnov@gmail.com>
// See THIRD-PARTY-NOTICE.libbsc.txt in this directory.
using System.Buffers.Binary;

namespace FileFormat.Bsc;

/// <summary>
/// The 16-bit range coder used by libbsc's QLFC entropy coders.
/// </summary>
internal sealed class BscRangeEncoder {
  private readonly byte[] _output;
  private readonly int _eob;
  private int _position;
  private ulong _low;
  private uint _ffCount;
  private uint _cache;
  private uint _range = uint.MaxValue;

  public BscRangeEncoder(int capacity) {
    if (capacity < 32)
      capacity = 32;
    _output = new byte[capacity];
    _eob = capacity - 16;
  }

  public bool CheckEndOfBuffer => _position >= _eob;

  public void EncodeBit0(int probability, int precision = 12) {
    Normalize();
    _range = unchecked((_range >> precision) * (uint)probability);
  }

  public void EncodeBit1(int probability, int precision = 12) {
    Normalize();
    var split = unchecked((_range >> precision) * (uint)probability);
    _low = unchecked(_low + split);
    _range = unchecked(_range - split);
  }

  public void EncodeBit(uint bit, int probability, int precision = 12) {
    if (bit == 0)
      EncodeBit0(probability, precision);
    else
      EncodeBit1(probability, precision);
  }

  public void EncodeBit(uint bit) => EncodeBit(bit, 2048);

  public void EncodeWord(uint value) {
    for (var bit = 31; bit >= 0; --bit)
      EncodeBit(value & (1u << bit));
  }

  public byte[] Finish() {
    if (_range < 0x10000)
      _range = ShiftLow();
    _range = ShiftLow();
    _range = ShiftLow();
    _range = ShiftLow();
    return _output.AsSpan(0, _position).ToArray();
  }

  private void Normalize() {
    if (_range < 0x10000)
      _range = ShiftLow();
  }

  private uint ShiftLow() {
    var low32 = (uint)_low;
    var carry = (uint)(_low >> 32);

    if (low32 < 0xffff0000u || carry != 0) {
      WriteUInt16(unchecked((ushort)(_cache + carry)));
      if (_ffCount != 0) {
        var fill = unchecked((ushort)(carry - 1));
        do {
          WriteUInt16(fill);
        } while (--_ffCount != 0);
      }

      _cache = low32 >> 16;
      _low = unchecked((uint)(low32 << 16));
    } else {
      ++_ffCount;
      _low = unchecked((uint)(low32 << 16));
    }

    return unchecked(_range << 16);
  }

  private void WriteUInt16(ushort value) {
    if (_position > _output.Length - sizeof(ushort))
      throw new InvalidDataException("BSC: QLFC range coder output overflow");
    BinaryPrimitives.WriteUInt16LittleEndian(_output.AsSpan(_position, sizeof(ushort)), value);
    _position += sizeof(ushort);
  }
}

/// <summary>
/// Decoder counterpart of <see cref="BscRangeEncoder"/>.
/// </summary>
internal ref struct BscRangeDecoder {
  private readonly ReadOnlySpan<byte> _input;
  private int _position;
  private uint _code;
  private uint _range;

  public BscRangeDecoder(ReadOnlySpan<byte> input) {
    _input = input;
    _position = 0;
    _code = 0;
    _range = uint.MaxValue;
    _code = unchecked((_code << 16) | ReadUInt16());
    _code = unchecked((_code << 16) | ReadUInt16());
    _code = unchecked((_code << 16) | ReadUInt16());
  }

  public int PeakBit(int probability, int precision = 12) {
    Normalize();
    return _code >= unchecked((_range >> precision) * (uint)probability) ? 1 : 0;
  }

  public int DecodeBit(int probability, int precision = 12) {
    Normalize();
    var split = unchecked((_range >> precision) * (uint)probability);
    var bit = _code >= split ? 1 : 0;
    if (bit == 0) {
      _range = split;
    } else {
      _code = unchecked(_code - split);
      _range = unchecked(_range - split);
    }
    return bit;
  }

  public void DecodeBit0(int probability, int precision = 12)
    => _range = unchecked((_range >> precision) * (uint)probability);

  public void DecodeBit1(int probability, int precision = 12) {
    var split = unchecked((_range >> precision) * (uint)probability);
    _code = unchecked(_code - split);
    _range = unchecked(_range - split);
  }

  public int DecodeBit() => DecodeBit(2048);

  public uint DecodeWord() {
    uint value = 0;
    for (var bit = 31; bit >= 0; --bit)
      value = unchecked(value + value + (uint)DecodeBit());
    return value;
  }

  private void Normalize() {
    if (_range >= 0x10000)
      return;
    _range = unchecked(_range << 16);
    _code = unchecked((_code << 16) | ReadUInt16());
  }

  private ushort ReadUInt16() {
    if (_position > _input.Length - sizeof(ushort))
      throw new InvalidDataException("BSC: truncated QLFC range stream");
    var value = BinaryPrimitives.ReadUInt16LittleEndian(_input[_position..]);
    _position += sizeof(ushort);
    return value;
  }
}
