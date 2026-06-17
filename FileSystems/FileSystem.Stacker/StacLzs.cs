#pragma warning disable CS1591
namespace FileSystem.Stacker;

/// <summary>
/// Stac LZS (Hi/fn) compression as published in IETF RFC 1967 / RFC 2395.
/// A bit-oriented LZ77 variant over a 2048-byte sliding history window with
/// MSB-first bit packing. This is the scheme Stacker uses for compressed
/// clusters inside a STACVOL.
/// </summary>
public static class StacLzs {
  private const int MaxOffset = 2047;

  private sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private int _bit; // number of bits filled in the current partial byte
    private int _cur;

    public void WriteBit(int value) {
      this._cur = (this._cur << 1) | (value & 1);
      if (++this._bit != 8)
        return;
      this._bytes.Add((byte)this._cur);
      this._cur = 0;
      this._bit = 0;
    }

    public void WriteBits(int value, int count) {
      for (var i = count - 1; i >= 0; --i)
        this.WriteBit((value >> i) & 1);
    }

    public byte[] ToArray() {
      if (this._bit > 0)
        this._bytes.Add((byte)(this._cur << (8 - this._bit)));
      return [.. this._bytes];
    }
  }

  private sealed class BitReader(byte[] data) {
    private int _pos;
    private int _bit;

    public bool Eof => this._pos >= data.Length;

    public int ReadBit() {
      if (this._pos >= data.Length)
        return -1;
      var v = (data[this._pos] >> (7 - this._bit)) & 1;
      if (++this._bit == 8) {
        this._bit = 0;
        ++this._pos;
      }
      return v;
    }

    public int ReadBits(int count) {
      var v = 0;
      for (var i = 0; i < count; ++i) {
        var b = this.ReadBit();
        if (b < 0)
          return -1;
        v = (v << 1) | b;
      }
      return v;
    }
  }

  /// <summary>Compress <paramref name="input"/> into a STORED-or-LZS stream.</summary>
  public static byte[] Compress(byte[] input) {
    ArgumentNullException.ThrowIfNull(input);
    var w = new BitWriter();
    var n = input.Length;
    var i = 0;
    while (i < n) {
      var (bestLen, bestOff) = FindMatch(input, i);
      if (bestLen >= 2) {
        w.WriteBit(1);
        WriteOffset(w, bestOff);
        WriteLength(w, bestLen);
        i += bestLen;
      } else {
        w.WriteBit(0);
        w.WriteBits(input[i], 8);
        ++i;
      }
    }

    // End marker: a long-offset escape with offset == 0.
    w.WriteBit(1);
    w.WriteBit(0);
    w.WriteBits(0, 11);
    return w.ToArray();
  }

  /// <summary>Decompress an LZS stream into exactly <paramref name="expectedLength"/> bytes.</summary>
  public static byte[] Decompress(byte[] input, int expectedLength) {
    ArgumentNullException.ThrowIfNull(input);
    var r = new BitReader(input);
    var output = new List<byte>(expectedLength > 0 ? expectedLength : 16);
    while (true) {
      var token = r.ReadBit();
      if (token < 0)
        break;
      if (token == 0) {
        var b = r.ReadBits(8);
        if (b < 0)
          break;
        output.Add((byte)b);
        if (expectedLength > 0 && output.Count >= expectedLength)
          break;
        continue;
      }

      var (offset, end) = ReadOffset(r);
      if (end)
        break;
      if (offset <= 0)
        break;
      var length = ReadLength(r);
      if (length <= 0)
        break;
      var start = output.Count - offset;
      if (start < 0)
        break;
      for (var k = 0; k < length; ++k)
        output.Add(output[start + k]);
      if (expectedLength > 0 && output.Count >= expectedLength)
        break;
    }

    var arr = output.ToArray();
    if (expectedLength > 0 && arr.Length != expectedLength)
      Array.Resize(ref arr, expectedLength);
    return arr;
  }

  private static (int len, int off) FindMatch(byte[] data, int pos) {
    var n = data.Length;
    var windowStart = Math.Max(0, pos - MaxOffset);
    var bestLen = 0;
    var bestOff = 0;
    for (var s = pos - 1; s >= windowStart; --s) {
      var len = 0;
      while (pos + len < n && data[s + len] == data[pos + len])
        ++len;
      if (len <= bestLen)
        continue;
      bestLen = len;
      bestOff = pos - s;
    }

    return (bestLen, bestOff);
  }

  private static void WriteOffset(BitWriter w, int offset) {
    if (offset <= 127) {
      w.WriteBit(1);
      w.WriteBits(offset, 7);
    } else {
      w.WriteBit(0);
      w.WriteBits(offset, 11);
    }
  }

  private static (int offset, bool end) ReadOffset(BitReader r) {
    var sel = r.ReadBit();
    if (sel < 0)
      return (0, true);
    if (sel == 1) {
      var off = r.ReadBits(7);
      return off < 0 ? (0, true) : (off, false);
    }

    var off11 = r.ReadBits(11);
    if (off11 < 0)
      return (0, true);
    return off11 == 0 ? (0, true) : (off11, false);
  }

  // Length encoding per RFC 1967/2395:
  //  00 -> 2, 01 -> 3, 10 -> 4,
  //  1100 -> 5, 1101 -> 6, 1110 -> 7,
  //  1111 0000 -> 8 .. 1111 1110 -> 22, then 1111 1111 + next nibble continues.
  private static void WriteLength(BitWriter w, int length) {
    switch (length) {
      case 2: w.WriteBits(0b00, 2); return;
      case 3: w.WriteBits(0b01, 2); return;
      case 4: w.WriteBits(0b10, 2); return;
    }

    if (length <= 7) {
      w.WriteBits(0b11, 2);
      w.WriteBits(length - 5, 2); // 5->00, 6->01, 7->10
      return;
    }

    w.WriteBits(0b11, 2);
    w.WriteBits(0b11, 2);
    var remaining = length - 8;
    while (remaining >= 15) {
      w.WriteBits(0xF, 4);
      remaining -= 15;
    }

    w.WriteBits(remaining, 4);
  }

  private static int ReadLength(BitReader r) {
    var two = r.ReadBits(2);
    switch (two) {
      case 0b00: return 2;
      case 0b01: return 3;
      case 0b10: return 4;
    }

    var next = r.ReadBits(2);
    if (next < 0)
      return -1;
    if (next != 0b11)
      return 5 + next; // 00->5, 01->6, 10->7

    var length = 8;
    while (true) {
      var nib = r.ReadBits(4);
      if (nib < 0)
        return -1;
      length += nib;
      if (nib != 0xF)
        return length;
    }
  }
}
