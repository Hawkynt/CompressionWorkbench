namespace FileFormat.StuffItX;

/// <summary>
/// Darkhorse, StuffIt X's method 2: LZ77 whose literals, lengths, distances and recent-distance
/// picks are coded bit by bit with adaptive 12-bit probabilities by the carry-less range coder.
/// </summary>
/// <remarks>
/// <para>
/// The stream opens with the base-2 logarithm of the window size (at least 1 MiB) and one skipped
/// byte; the range coder then starts. Each step codes a flag (weighted by the position modulo 4):
/// a literal, coded against the high nibble of the previous byte and, right after a match, guided
/// by the byte that would have followed the copy; or a match, either new (length, then a distance
/// coded by a six-bit slot) or one of four remembered distances. A length of 0x111 ends the stream.
/// A probability p (out of 4096) moves by (4096 - p) / 32 after a zero and by p / 32 after a one.
/// </para>
/// <para>Converted from XADMaster's <c>XADStuffItXDarkhorseHandle.m</c>, <c>XADLZSSHandle.m</c> and
/// <c>CarrylessRangeCoder.m</c> (LGPL-2.1-or-later, see THIRD-PARTY-NOTICE.XADMaster.txt).</para>
/// </remarks>
internal sealed class StuffItXDarkhorse {
  private const int EndLength = 0x111;
  private const uint Half = 0x800;

  private static readonly int[] DistanceBase = [
    0, 1, 2, 3, 4, 6, 8, 0xc, 0x10, 0x18, 0x20, 0x30, 0x40, 0x60, 0x80, 0xc0,
    0x100, 0x180, 0x200, 0x300, 0x400, 0x600, 0x800, 0xc00, 0x1000, 0x1800, 0x2000, 0x3000, 0x4000, 0x6000, 0x8000, 0xc000,
    0x10000, 0x18000, 0x20000, 0x30000, 0x40000, 0x60000, 0x80000, 0xc0000,
    0x100000, 0x180000, 0x200000, 0x300000, 0x400000, 0x600000, 0x800000, 0xc00000,
    0x1000000, 0x1800000, 0x2000000, 0x3000000, 0x4000000, 0x6000000, 0x8000000, 0xc000000,
    0x10000000, 0x18000000, 0x20000000, 0x30000000, 0, 0, 0, 0,
  ];

  private static readonly int[] DistanceBits = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9,
    10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18, 19, 19,
    20, 20, 21, 21, 22, 22, 23, 23, 24, 24, 25, 25, 26, 26, 27, 27, 28, 28, 0, 0, 0, 0,
  ];

  private readonly StuffItXRangeDecoder _coder;
  private readonly uint[] _flag = Filled(4);
  private uint _flag2 = Half;
  private readonly uint[] _literal = Filled(16 * 256);
  private readonly uint[] _literalGuided = Filled(16 * 256 * 2);
  private uint _recency1 = Half, _recency2 = Half, _recency3 = Half;
  private readonly uint[] _recency = Filled(4);
  private uint _lengthKind = Half;
  private readonly uint[] _shortLength = Filled(4 * 16);
  private readonly uint[] _longLength = Filled(256);
  private readonly uint[] _distanceSlot = Filled(4 * 64);
  private readonly uint[] _distance = Filled(10 * 32);
  private readonly uint[] _distanceLow = Filled(16);
  private readonly int[] _distances = new int[4];

  private StuffItXDarkhorse(StuffItXInput input) {
    input.ReadByte();
    this._coder = new StuffItXRangeDecoder(input, useLow: false, bottom: 0);
  }

  private static uint[] Filled(int count) {
    var a = new uint[count];
    Array.Fill(a, Half);
    return a;
  }

  /// <summary>
  /// Decodes a Darkhorse stream: its window-size byte, then the coded data, until the end code or
  /// <paramref name="length"/> bytes (−1: until the end code).
  /// </summary>
  public static byte[] Decode(StuffItXInput input, long length) {
    var windowBits = input.ReadByte();
    if (windowBits > 31) throw new InvalidDataException("Darkhorse window size is out of range.");
    return new StuffItXDarkhorse(input).Run(length);
  }

  private byte[] Run(long length) {
    var output = new List<byte>(length > 0 ? (int)Math.Min(length, 1 << 24) : 1 << 16);
    var next = -1;
    while (length < 0 || output.Count < length) {
      var pos = output.Count;
      if (this.Bit(ref this._flag[pos & 3]) == 0) {
        output.Add((byte)this.Literal(At(output, pos - 1), next));
        next = -1;
        continue;
      }

      int len, distance;
      if (this.Bit(ref this._flag2) == 0) {
        len = this.Length(pos & 3) + 2;
        if (len == EndLength) break;
        distance = this.Distance(len);
        this.Remember(3, distance);
      } else {
        var recency = this.Recency(pos & 3);
        if (recency < 0) {
          distance = this._distances[0];
          len = 1;
        } else {
          distance = this._distances[recency];
          this.Remember(recency, distance);
          len = this.Length(pos & 3) + 2;
        }
      }

      // The guide for the next literal is the byte after the copy, as the window holds it now.
      next = At(output, pos - distance - 1 + len % (distance + 1));
      var from = pos - distance - 1;
      for (var i = 0; i < len && (length < 0 || output.Count < length); ++i)
        output.Add((byte)At(output, from + i));
    }
    return [.. output];
  }

  /// <summary>A byte already produced; before the start the window holds zeros.</summary>
  private static int At(List<byte> output, int index) => index >= 0 && index < output.Count ? output[index] : 0;

  private int Bit(ref uint weight) {
    var bit = this._coder.NextWeightedBitShift(weight, 12);
    if (bit == 0) weight += (0x1000 - weight) >> 5;
    else weight -= weight >> 5;
    return bit;
  }

  private int Literal(int previous, int guess) {
    var context = (previous >> 4) * 256;
    var value = 1;
    if (guess >= 0) {
      while (value < 0x100) {
        var guessBit = (guess >> 7) & 1;
        var bit = this.Bit(ref this._literalGuided[(context + value) * 2 + guessBit]);
        value = (value << 1) | bit;
        if (bit != guessBit) break;
        guess <<= 1;
      }
    }
    while (value < 0x100) value = (value << 1) | this.Bit(ref this._literal[context + value]);
    return value & 0xFF;
  }

  private int Length(int index)
    => this.Bit(ref this._lengthKind) == 0
      ? this.Symbol(this._shortLength, index * 16, 4)
      : this.Symbol(this._longLength, 0, 8) + 16;

  private int Distance(int len) {
    var slot = this.Symbol(this._distanceSlot, Math.Min(len - 2, 3) * 64, 6);
    if (slot < 4) return slot;
    if (slot < 14) return DistanceBase[slot] + this.Symbol(this._distance, (slot - 4) * 32, DistanceBits[slot]);
    var value = 0;
    for (var i = DistanceBits[slot] - 1; i >= 4; --i) value |= this._coder.NextBit() << i;
    return value + DistanceBase[slot] + this.Symbol(this._distanceLow, 0, 4);
  }

  private int Recency(int index) {
    if (this.Bit(ref this._recency1) == 0) return this.Bit(ref this._recency[index]) == 0 ? -1 : 0;
    if (this.Bit(ref this._recency2) == 0) return 1;
    return this.Bit(ref this._recency3) == 0 ? 2 : 3;
  }

  /// <summary>A <paramref name="bits"/>-bit number coded most significant bit first through a binary tree of weights.</summary>
  private int Symbol(uint[] weights, int baseIndex, int bits) {
    var value = 1;
    for (var i = 0; i < bits; ++i) value = (value << 1) | this.Bit(ref weights[baseIndex + value]);
    return value - (1 << bits);
  }

  private void Remember(int oldIndex, int distance) {
    for (var i = oldIndex; i > 0; --i) this._distances[i] = this._distances[i - 1];
    this._distances[0] = distance;
  }
}
