using System.Buffers.Binary;

namespace FileFormat.Lhf;

/// <summary>
/// The LhF 1.03 compressor's decoder: blocks of canonical-Huffman-coded literals and matches over a
/// 32 KiB window, read as big-endian 16-bit words, most-significant bit first.
/// </summary>
/// <remarks>
/// <para>A block is a 16-bit symbol count, the literal/length code, the distance-slot code when the
/// literal/length code has symbols above 255, then the symbols; one bit after each block says whether
/// another block follows. Code lengths travel as in LHA's <c>-lh5-</c> family: a small pre-code (5-bit
/// count, 3-bit lengths with a unary extension) codes the literal/length lengths with run symbols for
/// zeros and for repeating the previous length. Symbol <c>s ≥ 256</c> copies <c>s − 253</c> bytes;
/// distance slot <c>d</c> means <c>d + 1</c> for <c>d ≤ 1</c>, else <c>2^(d−1) + bits(d−1) + 1</c>.
/// Bytes before the start of the file read as zero. See <c>docs/LHF-ON-DISK.md</c>.</para>
/// <para>Sourcing: converted from libxad's LhF client (XADMaster <c>libxad/clients/LhF.c</c>,
/// LGPL-2.1-or-later), checked against the files LhF 1.03 itself packed and unpacked.</para>
/// </remarks>
public static class LhfDecoder {
  private const int WindowSize = 0x8000;

  /// <summary>Unpacks <paramref name="source"/> into exactly <paramref name="size"/> bytes.</summary>
  /// <exception cref="InvalidDataException">The stream is damaged or does not unpack to <paramref name="size"/> bytes.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> source, int size) {
    var bits = new BitReader(source);
    var output = new byte[size];
    var o = 0;
    do {
      var count = bits.Get(16);
      if (count == 0)
        throw new InvalidDataException("LhF: empty block.");

      Code literals;
      int highest;
      var codeCount = bits.Get(9);
      if (codeCount != 0) {
        var pre = ReadSmallCode(ref bits, 5, 20);
        var lengths = new List<int>(512);
        for (var k = 0; k <= codeCount; ++k) {
          var s = pre.Decode(ref bits);
          switch (s) {
            case 0:
              if (lengths.Count == 0)
                throw new InvalidDataException("LhF: repeat with no previous length.");
              var repeat = bits.Get(3) + 3;
              var last = lengths[^1];
              for (var r = 0; r < repeat; ++r) lengths.Add(last);
              break;
            case 1:
              lengths.AddRange(Enumerable.Repeat(0, bits.Get(2) + 4));
              break;
            case 2:
              lengths.AddRange(Enumerable.Repeat(0, bits.Get(7) + 8));
              break;
            default:
              lengths.Add(s - 3);
              break;
          }
        }
        while (lengths.Count > 0 && lengths[^1] == 0) lengths.RemoveAt(lengths.Count - 1);
        if (lengths.Count == 0 || lengths.Count > 512)
          throw new InvalidDataException("LhF: bad literal/length code.");
        highest = lengths.Count - 1;
        literals = Code.Canonical(lengths);
      } else {
        highest = bits.Get(9);
        literals = Code.Single(highest);
      }

      var distances = highest >= 256 ? ReadSmallCode(ref bits, 4, 16) : Code.Single(0);

      for (var n = 0; n < count; ++n) {
        var c = literals.Decode(ref bits);
        if (c < 256) {
          if (o >= size)
            throw new InvalidDataException("LhF: data runs past the file size.");
          output[o++] = (byte)c;
          continue;
        }
        var slot = distances.Decode(ref bits);
        var distance = (slot <= 1 ? slot : (1 << (slot - 1)) + bits.Get(slot - 1)) + 1;
        var length = c - 253;
        if (o + length > size)
          throw new InvalidDataException("LhF: match runs past the file size.");
        for (var l = 0; l < length; ++l, ++o)
          output[o] = o >= distance ? output[o - distance] : (byte)0;
      }
    } while (bits.Get(1) != 0);

    if (o != size)
      throw new InvalidDataException($"LhF: unpacked {o} bytes, expected {size}.");
    return output;
  }

  // Pre-code / distance code: a count (bits wide), then lengths of 3 bits extended in unary past 7.
  private static Code ReadSmallCode(ref BitReader bits, int width, int maxSymbols) {
    var n = bits.Get(width);
    if (n == 0) {
      var only = bits.Get(width);
      if (only >= maxSymbols)
        throw new InvalidDataException("LhF: bad single-symbol code.");
      return Code.Single(only);
    }
    if (n >= maxSymbols)
      throw new InvalidDataException("LhF: code has too many symbols.");
    var lengths = new int[n + 1];
    for (var i = 0; i <= n; ++i) {
      var k = bits.Get(3);
      if (k == 7)
        while (bits.Get(1) != 0)
          if (++k > 16)
            throw new InvalidDataException("LhF: code length too long.");
      lengths[i] = k;
    }
    return Code.Canonical(lengths);
  }

  /// <summary>Canonical prefix code: shorter codes first, symbols ascending within a length.</summary>
  private sealed class Code {
    private readonly int _single = -1;
    private readonly int[] _firstCode = new int[17];
    private readonly int[] _firstIndex = new int[17];
    private readonly int[] _count = new int[17];
    private readonly int[] _symbols = [];

    private Code(int single) => this._single = single;

    private Code(IReadOnlyList<int> lengths) {
      var symbols = new List<int>();
      int code = 0;
      for (var len = 1; len <= 16; ++len) {
        this._firstCode[len] = code;
        this._firstIndex[len] = symbols.Count;
        for (var s = 0; s < lengths.Count; ++s)
          if (lengths[s] == len) {
            symbols.Add(s);
            ++code;
            ++this._count[len];
          }
        if (code > 1 << len)
          throw new InvalidDataException("LhF: over-subscribed prefix code.");
        code <<= 1;
      }
      if (symbols.Count == 0)
        throw new InvalidDataException("LhF: prefix code has no symbols.");
      this._symbols = [.. symbols];
    }

    public static Code Single(int symbol) => new(symbol);

    public static Code Canonical(IReadOnlyList<int> lengths) => new(lengths);

    public int Decode(ref BitReader bits) {
      if (this._single >= 0)
        return this._single;
      var code = 0;
      for (var len = 1; len <= 16; ++len) {
        code = (code << 1) | bits.Get(1);
        var offset = code - this._firstCode[len];
        if (offset >= 0 && offset < this._count[len])
          return this._symbols[this._firstIndex[len] + offset];
      }
      throw new InvalidDataException("LhF: invalid prefix code.");
    }
  }

  /// <summary>Big-endian 16-bit words, most-significant bit first; zero past the end, as LhF reads it.</summary>
  private ref struct BitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;
    private uint _buffer;
    private int _count;

    public int Get(int n) {
      if (n == 0)
        return 0;
      while (this._count < n) {
        var word = this._position + 2 <= this._data.Length ? BinaryPrimitives.ReadUInt16BigEndian(this._data[this._position..]) : 0;
        this._position += 2;
        if (this._position > this._data.Length + 64)
          throw new InvalidDataException("LhF: compressed data ends early.");
        this._buffer = (this._buffer << 16) | word;
        this._count += 16;
      }
      this._count -= n;
      var value = (int)(this._buffer >> this._count) & ((1 << n) - 1);
      this._buffer &= (1u << this._count) - 1;
      return value;
    }
  }
}
