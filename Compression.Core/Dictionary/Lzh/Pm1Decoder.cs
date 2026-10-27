namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes PMarc <c>-pm1-</c>: alternating runs of literals and copies over a 16 KiB window, all
/// coded with fixed (static) prefix codes whose reach grows with the amount of output.
/// </summary>
/// <remarks>
/// <para>A 5-bit header selects one of 32 small trees that code the literal-range index; literals are
/// distances along a move-to-front history (<see cref="PmaHistory"/>). Each command is either a copy
/// (flag 0) or a block of 1-216 literals that, unless at its maximum length, is followed by a copy.
/// Copy distances use ranges that open up after 64, 320, 576, 832, ... bytes of output.</para>
/// <para>Sourcing: converted from lhasa's <c>pm1_decoder.c</c> (ISC, Simon Howard; reverse-engineered
/// there from the UNPMA10 decoder), tables copied exactly; see THIRD-PARTY-NOTICE.lhasa.txt beside this
/// file. Checked against the <c>-pm1-</c> archives of lhasa's test suite. Like the reference, input past
/// the end of the member reads as zero bytes.</para>
/// </remarks>
public static class Pm1Decoder {
  private const int RingSize = 16384;
  private const int MaxByteBlock = 216;

  private static readonly (int Offset, int Bits)[] CopyRanges = [
    (0, 6), (64, 8), (0, 6), (64, 9), (576, 11), (2624, 13),
    (64, 8),
    (576, 8), (576, 9), (576, 10),
    (2624, 8), (2624, 9), (2624, 10), (2624, 11), (2624, 12),
  ];

  private static readonly (int Offset, int Bits)[] ByteRanges = [(0, 4), (16, 4), (32, 5), (64, 6), (128, 6), (192, 6)];

  private static readonly byte[][] ByteDecodeTrees = [
    [0x12, 0x2d, 0xef, 0x1c, 0xab], [0x12, 0x23, 0xde, 0xab, 0xcf], [0x12, 0x2c, 0xd2, 0xab, 0xef],
    [0x12, 0xa2, 0xd2, 0xbc, 0xef], [0x12, 0xa2, 0xc2, 0xbd, 0xef], [0x12, 0xa2, 0xcd, 0xb1, 0xef],
    [0x12, 0xab, 0x12, 0xcd, 0xef], [0x12, 0xab, 0x1d, 0xc1, 0xef], [0x12, 0xab, 0xc1, 0xd1, 0xef],
    [0xa1, 0x12, 0x2c, 0xde, 0xbf], [0xa1, 0x1d, 0x1c, 0xb1, 0xef], [0xa1, 0x12, 0x2d, 0xef, 0xbc],
    [0xa1, 0x12, 0xb2, 0xde, 0xcf], [0xa1, 0x12, 0xbc, 0xd1, 0xef], [0xa1, 0x1c, 0xb1, 0xd1, 0xef],
    [0xa1, 0xb1, 0x12, 0xcd, 0xef], [0xa1, 0xb1, 0xc1, 0xd1, 0xef], [0x12, 0x1c, 0xde, 0xab],
    [0x12, 0xa2, 0xcd, 0xbe], [0x12, 0xab, 0xc1, 0xde], [0xa1, 0x1d, 0x1c, 0xbe],
    [0xa1, 0x12, 0xbc, 0xde], [0xa1, 0x1c, 0xb1, 0xde], [0xa1, 0xb1, 0xc1, 0xde],
    [0x1d, 0x1c, 0xab], [0x1c, 0xa1, 0xbd], [0x12, 0xab, 0xcd],
    [0xa1, 0x1c, 0xbd], [0xa1, 0xb1, 0xcd], [0xa1, 0xbc],
    [0xab], [0x00],
  ];

  /// <summary>Decodes <paramref name="originalSize"/> bytes of <c>-pm1-</c> data.</summary>
  /// <exception cref="InvalidDataException">The stream is damaged.</exception>
  public static byte[] Decode(byte[] compressed, int originalSize) {
    ArgumentNullException.ThrowIfNull(compressed);
    ArgumentOutOfRangeException.ThrowIfNegative(originalSize);
    return new State(compressed, originalSize).Run();
  }

  private sealed class State(byte[] compressed, int originalSize) {
    private readonly LhaBitInput _bits = new(compressed);
    private readonly byte[] _output = new byte[originalSize];
    private readonly byte[] _ring = new byte[RingSize];
    private readonly PmaHistory _history = new();
    private byte[] _byteTree = [];
    private int _ringPos;
    private int _pos;  // bytes output so far

    public byte[] Run() {
      if (this._output.Length == 0)
        return this._output;
      this._byteTree = ByteDecodeTrees[this._bits.ReadBits(5)];
      var budget = (long)compressed.Length * 8 + 64 * 1024;  // zero-fill reads past the end; bound a runaway
      while (this._pos < this._output.Length) {
        if (--budget < 0)
          throw new InvalidDataException("-pm1- stream does not produce its declared size.");
        if (this._bits.ReadBit() == 0)
          this.CopyCommand();
        else
          this.ByteBlock();
      }
      return this._output;
    }

    private void Output(byte b) {
      this._ring[this._ringPos] = b;
      this._ringPos = (this._ringPos + 1) % RingSize;
      this._history.Update(b);
      if (this._pos < this._output.Length)
        this._output[this._pos] = b;
      ++this._pos;
    }

    private int BitAfter(int threshold, int fallback) => this._pos >= threshold ? this._bits.ReadBit() : fallback;

    private int CopyTypeRange() {
      if (this._bits.ReadBit() == 0)
        return this.BitAfter(576, 0) != 0 ? 4 : this.BitAfter(64, 0);
      if (this.BitAfter(64, 1) == 0)
        return 3;
      return this.BitAfter(2624, 1) != 0 ? 2 : 5;
    }

    private int CopyCount() {
      var x = this._bits.ReadBits(2);
      if (x < 3) return x + 3;
      x = this._bits.ReadBits(3);
      if (x < 5) return x + 6;
      if (x == 5) return this._bits.ReadBits(2) + 11;
      if (x == 6) return this._bits.ReadBits(3) + 15;
      x = this._bits.ReadBits(6);
      if (x < 62) return x + 23;
      return x == 62 ? this._bits.ReadBits(5) + 85 : this._bits.ReadBits(7) + 117;
    }

    private void CopyCommand() {
      var range = this.CopyTypeRange();
      var count = range < 2 ? 2 : this.CopyCount();
      range = range switch {
        3 when this._pos < 320 => 6,
        4 when this._pos < 832 => 7,
        4 when this._pos < 1088 => 8,
        4 when this._pos < 1600 => 9,
        5 when this._pos < 2880 => 10,
        5 when this._pos < 3136 => 11,
        5 when this._pos < 3648 => 12,
        5 when this._pos < 4672 => 13,
        5 when this._pos < 6720 => 14,
        _ => range,
      };
      var distance = CopyRanges[range].Offset + this._bits.ReadBits(CopyRanges[range].Bits);
      if (distance >= this._pos)
        throw new InvalidDataException("-pm1- copy reaches before the start of the output.");
      var index = (this._ringPos + RingSize - distance - 1) % RingSize;
      for (var i = 0; i < count; ++i) {
        this.Output(this._ring[index]);
        index = (index + 1) % RingSize;
      }
    }

    private int ByteDecodeIndex() {
      var tree = this._byteTree;
      if (tree[0] == 0)
        return 0;
      var at = 0;
      while (true) {
        var child = this._bits.ReadBit() == 0 ? (tree[at] >> 4) & 0x0F : tree[at] & 0x0F;
        if (child >= 10)
          return child - 10;
        at += child;
        if (at >= tree.Length)
          throw new InvalidDataException("-pm1- literal tree walk leaves its table.");
      }
    }

    private int ByteBlockCount() {
      var x = this._bits.ReadBits(2);
      if (x < 3) return x + 1;
      x = this._bits.ReadBits(3);
      if (x < 7) return x + 4;
      x = this._bits.ReadBits(4);
      if (x < 14) return x + 11;
      return x == 14 ? this._bits.ReadBits(6) + 25 : this._bits.ReadBits(7) + 89;
    }

    private void ByteBlock() {
      var length = this.ByteBlockCount();
      for (var i = 0; i < length; ++i) {
        var index = this.ByteDecodeIndex();
        if (index >= ByteRanges.Length)
          throw new InvalidDataException("-pm1- literal range is out of range.");
        var count = ByteRanges[index].Offset + this._bits.ReadBits(ByteRanges[index].Bits);
        this.Output(this._history.Find(count));
      }
      if (length != MaxByteBlock && this._pos < this._output.Length)
        this.CopyCommand();
    }
  }
}
