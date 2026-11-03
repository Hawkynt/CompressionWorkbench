using System.Buffers.Binary;

namespace FileFormat.FreeArc;

/// <summary>
/// Decoder for FreeArc's Tornado (<c>tor</c>) stage, written from docs/FREEARC-TORNADO.md: an LZ77
/// stream behind a byte, bit, semi-adaptive Huffman or semi-adaptive arithmetic coder, with
/// optional differenced data tables.
/// </summary>
internal static class FreeArcTornado {
  private const int ImpossibleLen = int.MaxValue / 2;
  private const int ImpossibleDist = int.MaxValue / 2;
  private const int RepDistCodes = 4;
  private const int LenCodes = 16;
  private const int EobCode = 256 + LenCodes * (32 + RepDistCodes); // 832
  private const int RepChar = EobCode + 1;
  private const int Codes = EobCode + 10;

  private static readonly int[] BitLenExtra = [0, 0, 0, 1, 2, 4, 8, 30];
  private static readonly int[] LenExtra = [0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 8, 30];
  private static readonly int[] DistExtra = [4, 4, 5, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 17, 18, 19, 21, 23, 30];
  private static readonly uint[] BitLenBase = RunningBases(BitLenExtra);
  private static readonly uint[] LenBase = RunningBases(LenExtra);
  private static readonly uint[] DistBase = DistanceBases();

  private static uint[] RunningBases(int[] extra) {
    var bases = new uint[extra.Length];
    var v = 0L;
    for (var i = 0; i < extra.Length; ++i) {
      bases[i] = (uint)v;
      v += 1L << extra[i];
    }
    return bases;
  }

  /// <summary>Distance bases in three bands of 1, 256 and 65536 units, as the stream lays them out.</summary>
  private static uint[] DistanceBases() {
    var bases = new uint[DistExtra.Length];
    long dist = 0;
    var code = 0;
    for (; dist < 512; ++code) {
      bases[code] = (uint)dist;
      dist += 1L << DistExtra[code];
    }
    dist >>= 8;
    for (; dist < 512; ++code) {
      bases[code] = (uint)(dist << 8);
      dist += 1L << (DistExtra[code] - 8);
    }
    dist >>= 8;
    for (; code < DistExtra.Length; ++code) {
      bases[code] = (uint)(dist << 16);
      dist += 1L << (DistExtra[code] - 16);
    }
    return bases;
  }

  /// <summary>Decodes one Tornado stream (header included) to exactly <paramref name="expectedSize"/> bytes when that is known (≥ 0).</summary>
  public static byte[] Decode(ReadOnlySpan<byte> input, long expectedSize) {
    if (input.Length < 6)
      throw new InvalidDataException("FreeArc tor: stream is shorter than its header.");
    var coder = input[0];
    var minLen = input[1];
    var dictionary = BinaryPrimitives.ReadUInt32LittleEndian(input[2..]);
    var body = input[6..].ToArray();
    ISymbolSource source = coder switch {
      1 => new ByteSource(body, minLen),
      2 => new BitSource(body, minLen),
      3 => new CodedSource(new HuffmanSymbols(body), minLen),
      4 => new CodedSource(new ArithmeticSymbols(body), minLen),
      _ => throw new InvalidDataException($"FreeArc tor: unknown coder {coder}."),
    };

    var limit = expectedSize >= 0 ? expectedSize : long.MaxValue;
    var output = new Output(expectedSize);
    var tables = new List<(int N, long Start, long Rows)>();
    while (true) {
      if (source.IsLiteral()) {
        output.Add(source.Literal());
      } else {
        var len = source.Length();
        var dist = source.Distance();
        if (len == ImpossibleLen && dist == ImpossibleDist)
          break;
        if (len > ImpossibleLen) {
          var n = len - ImpossibleLen;
          if (n is < 1 or > 4 || dist == 0 || (long)n * dist > 2L * dictionary + 1)
            throw new InvalidDataException("FreeArc tor: invalid data table record.");
          tables.Add((n, output.Length, dist));
          continue;
        }
        if (len >= ImpossibleLen || dist == 0 || dist > output.Length || (dictionary > 0 && dist > dictionary))
          throw new InvalidDataException($"FreeArc tor: match distance {dist} reaches outside the window.");
        output.Copy(dist, len);
      }
      if (output.Length > limit)
        throw new InvalidDataException("FreeArc tor: stream decodes past its block.");
      source.CheckInput();
    }

    var data = output.ToArray();
    foreach (var (n, start, rows) in tables) {
      if (start + n * rows > data.Length)
        throw new InvalidDataException("FreeArc tor: data table runs past the output.");
      Undiff(data.AsSpan((int)start, (int)(n * rows)), n, (int)rows);
    }
    return data;
  }

  private static void Undiff(Span<byte> t, int n, int rows) {
    switch (n) {
      case 2: {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(t);
        for (var r = 1; r < rows; ++r) {
          v += BinaryPrimitives.ReadUInt16LittleEndian(t[(r * 2)..]);
          BinaryPrimitives.WriteUInt16LittleEndian(t[(r * 2)..], v);
        }
        break;
      }
      case 4: {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(t);
        for (var r = 1; r < rows; ++r) {
          v += BinaryPrimitives.ReadUInt32LittleEndian(t[(r * 4)..]);
          BinaryPrimitives.WriteUInt32LittleEndian(t[(r * 4)..], v);
        }
        break;
      }
      default:
        for (var r = n; r < n * rows; r += n) {
          var carry = 0;
          for (var i = 0; i < n; ++i) {
            var sum = t[r + i] + t[r + i - n] + carry;
            t[r + i] = (byte)sum;
            carry = sum >> 8;
          }
        }
        break;
    }
  }

  private sealed class Output(long expected) {
    private byte[] _data = new byte[expected is > 0 and < 1L << 30 ? expected : 1 << 16];
    public long Length { get; private set; }

    private void Ensure(long more) {
      var need = this.Length + more;
      if (need > Array.MaxLength)
        throw new InvalidDataException("FreeArc tor: output exceeds the supported size.");
      if (need > this._data.Length)
        Array.Resize(ref this._data, (int)Math.Min(Array.MaxLength, Math.Max(need, this._data.Length * 2L)));
    }

    public void Add(byte b) {
      this.Ensure(1);
      this._data[this.Length++] = b;
    }

    public void Copy(int dist, int len) {
      this.Ensure(len);
      var to = (int)this.Length;
      var from = to - dist;
      for (var k = 0; k < len; ++k)
        this._data[to + k] = this._data[from + k];
      this.Length += len;
    }

    public byte[] ToArray() => this._data.AsSpan(0, (int)this.Length).ToArray();
  }

  // ── symbol sources ────────────────────────────────────────────────────────

  private interface ISymbolSource {
    bool IsLiteral();
    byte Literal();
    int Length();
    int Distance();
    void CheckInput();
  }

  /// <summary>Little-endian byte reader that yields zeros a little past the end, then fails.</summary>
  private class Bytes(byte[] data) {
    protected readonly byte[] Data = data;
    protected int Pos;

    public uint Get8() => this.Pos < this.Data.Length ? this.Data[this.Pos++] : this.Past(1);
    public uint Get16() => this.Get8() | this.Get8() << 8;
    public uint Get24() => this.Get16() | this.Get8() << 16;
    public uint Get32() => this.Get24() | this.Get8() << 24;

    private uint Past(int n) {
      this.Pos += n;
      if (this.Pos > this.Data.Length + 16)
        throw new InvalidDataException("FreeArc tor: stream ends before its end marker.");
      return 0;
    }

    public void CheckInput() {
      if (this.Pos > this.Data.Length + 16)
        throw new InvalidDataException("FreeArc tor: stream ends before its end marker.");
    }
  }

  private sealed class ByteSource(byte[] data, int minLen) : Bytes(data), ISymbolSource {
    private uint _flags;
    private int _flagPos = 1;
    private int _dist;

    public bool IsLiteral() {
      if (--this._flagPos != 0)
        this._flags >>= 2;
      else {
        this._flagPos = 16;
        this._flags = this.Get32();
      }
      return (this._flags & 3) == 0;
    }

    public byte Literal() => (byte)this.Get8();

    public int Length() {
      uint len, dist;
      switch (this._flags & 3) {
        case 1: {
          var x = this.Get16();
          len = x >> 12;
          dist = x & 0xFFF;
          break;
        }
        case 2: {
          var x = this.Get24();
          len = x >> 18;
          dist = x & 0x3FFFF;
          break;
        }
        default: {
          len = this.Get8();
          if (len == 255) {
            dist = this.Get8() << 24;
            len = this.Get8();
          } else
            dist = 0;
          if (len == 254)
            len = (this.Get24() << 8) + this.Get8();
          dist += this.Get24();
          break;
        }
      }
      this._dist = (int)dist;
      return (int)Math.Min(int.MaxValue, (long)minLen + len);
    }

    public int Distance() => this._dist;
  }

  /// <summary>Bit reader: least-significant bit first, refilled 32 bits at a time.</summary>
  private class Bits(byte[] data) : Bytes(data) {
    private ulong _buf;
    private int _count;

    public uint Need(int n) {
      if (this._count <= 32) {
        this._buf |= (ulong)this.Get32() << this._count;
        this._count += 32;
      }
      return (uint)(this._buf & ((1UL << n) - 1));
    }

    public void Dump(int n) {
      this._buf >>= n;
      this._count -= n;
    }

    public uint GetBits(int n) {
      if (n == 0)
        return 0;
      var x = this.Need(n);
      this.Dump(n);
      return x;
    }
  }

  private sealed class BitSource(byte[] data, int minLen) : Bits(data), ISymbolSource {
    private uint _x;

    public bool IsLiteral() {
      this._x = this.GetBits(9);
      return this._x < 256;
    }

    public byte Literal() => (byte)this._x;

    public int Length() {
      var lcode = (int)(this._x >> 5) - 8;
      return (int)Math.Min(int.MaxValue, (long)minLen + BitLenBase[lcode] + this.GetBits(BitLenExtra[lcode]));
    }

    public int Distance() {
      var dcode = (int)(this._x & 31);
      return (int)Math.Min(int.MaxValue, (long)DistBase[dcode] + this.GetBits(DistExtra[dcode]));
    }
  }

  private interface ISymbols {
    int Decode();
    uint GetBits(int n);
    void CheckInput();
  }

  /// <summary>The Huffman/arithmetic LZ77 layer: literals, coded matches, repeat distances, table records.</summary>
  private sealed class CodedSource(ISymbols symbols, int minLen) : ISymbolSource {
    private readonly int[] _recent = new int[RepDistCodes]; // [0] = most recent
    private int _x;

    public bool IsLiteral() {
      this._x = symbols.Decode();
      if (this._x > RepChar)
        throw new InvalidDataException($"FreeArc tor: symbol {this._x} is not used by the format.");
      return this._x < 256;
    }

    public byte Literal() => (byte)this._x;

    public int Length() {
      if (this._x == RepChar)
        return 1;
      var lcode = this._x % LenCodes;
      var l = (long)LenBase[lcode] + symbols.GetBits(LenExtra[lcode]);
      if (l > 100)
        return l <= 104 ? (int)(l - 100 + ImpossibleLen) : (int)Math.Min(int.MaxValue, l - 4 + minLen);
      return (int)(l + minLen);
    }

    public int Distance() {
      if (this._x == RepChar)
        return this._recent[0];
      var dcode = (this._x - 256) / LenCodes - RepDistCodes;
      if (dcode < 0) {
        var k = dcode + RepDistCodes; // 0..3
        var d = this._recent[k];
        for (var i = k; i > 0; --i)
          this._recent[i] = this._recent[i - 1];
        this._recent[0] = d;
        return d;
      }
      var dist = (long)DistBase[dcode] + symbols.GetBits(DistExtra[dcode]) + 1;
      if (dist > int.MaxValue)
        throw new InvalidDataException("FreeArc tor: distance out of range.");
      for (var i = RepDistCodes - 1; i > 0; --i)
        this._recent[i] = this._recent[i - 1];
      this._recent[0] = (int)dist;
      return (int)dist;
    }

    public void CheckInput() => symbols.CheckInput();
  }

  // ── semi-adaptive Huffman ─────────────────────────────────────────────────

  private sealed class HuffmanSymbols : Bits, ISymbols {
    private const int FastBits = 11;
    private readonly int _n = Codes;
    private readonly uint[] _counter = new uint[Codes];
    private readonly byte[] _bits = new byte[Codes];
    private readonly int[] _fast = new int[1 << FastBits];
    private ushort[] _index = [];
    private int _maxBits;

    public HuffmanSymbols(byte[] data) : base(data) {
      Array.Fill(this._counter, 1u);
      this.Build(0);
    }

    public int Decode() {
      while (true) {
        var x = this.Lookup(this.Need(this._maxBits));
        this.Dump(this._bits[x]);
        if (x != EobCode) {
          ++this._counter[x];
          return x;
        }
        this.Build((int)this.GetBits(3));
      }
    }

    private int Lookup(uint code) {
      var x = this._fast[code & ((1u << FastBits) - 1)];
      return x >= 0 ? x : this._index[code];
    }

    private void Build(int rescaleMode) {
      var n = this._n;
      // Leaves stable-sorted by (counter, symbol).
      var order = Enumerable.Range(0, n).OrderBy(i => this._counter[i]).ThenBy(i => i).ToArray();
      var size = 2 * n + 4;
      var cnt = new ulong[size];
      var left = new int[size];
      var right = new int[size];
      var depth = new int[size];
      var code = new uint[size];
      for (var i = 0; i < n; ++i) {
        cnt[i] = this._counter[order[i]];
        left[i] = order[i];
      }
      for (var i = n; i < size; ++i)
        cnt[i] = int.MaxValue;
      int p1 = 0, p2 = n + 2, p3 = n + 2;
      while (!(p1 == n && p3 - p2 == 1)) {
        if (cnt[p1 + 1] < cnt[p2]) {
          cnt[p3] = cnt[p1] + cnt[p1 + 1];
          left[p3] = p1;
          right[p3] = p1 + 1;
          p1 += 2;
        } else if (cnt[p1] > cnt[p2 + 1]) {
          cnt[p3] = cnt[p2] + cnt[p2 + 1];
          left[p3] = p2;
          right[p3] = p2 + 1;
          p2 += 2;
        } else {
          cnt[p3] = cnt[p1] + cnt[p2];
          left[p3] = p1;
          right[p3] = p2;
          ++p1;
          ++p2;
        }
        ++p3;
      }
      depth[p2] = 0;
      code[p2] = 0;
      for (var i = p2; i >= n + 2; --i) {
        depth[left[i]] = depth[i] + 1;
        code[left[i]] = code[i];
        depth[right[i]] = depth[i] + 1;
        code[right[i]] = code[i] + (1u << depth[i]);
      }
      this._maxBits = depth[0];
      if (this._maxBits > 30)
        throw new InvalidDataException("FreeArc tor: Huffman code too long.");
      if (this._index.Length < 1 << this._maxBits)
        this._index = new ushort[1 << this._maxBits];
      for (var i = 0; i < n; ++i) {
        var s = left[i];
        var sbits = depth[i];
        var scode = code[i];
        this._bits[s] = (byte)sbits;
        if (sbits <= FastBits) {
          for (var j = 0; j < 1 << (FastBits - sbits); ++j)
            this._fast[scode + (j << sbits)] = s;
        } else {
          this._fast[scode & ((1u << FastBits) - 1)] = -1;
          for (var j = 0; j < 1 << (this._maxBits - sbits); ++j)
            this._index[scode + (j << sbits)] = (ushort)s;
        }
      }
      uint factor = rescaleMode switch { 0 => 2, 1 => 3, 2 => 4, 3 => 6, 4 => 8, 5 => 10, 6 => 12, _ => 16 };
      for (var s = 0; s < n; ++s) {
        var c = this._counter[s];
        this._counter[s] -= c > 1 && c < factor ? 1 : c / factor;
      }
    }

    uint ISymbols.GetBits(int n) => this.GetBits(n);
  }

  // ── semi-adaptive arithmetic ──────────────────────────────────────────────

  private sealed class ArithmeticSymbols : Bytes, ISymbols {
    private const int RangeBits = 14;
    private const uint Total = 1u << RangeBits;
    private const int Indexes = 2048;
    private readonly int _n = Codes;
    private readonly uint[] _cnt = new uint[Codes];
    private readonly uint[] _cum = new uint[Codes];
    private readonly uint[] _live = new uint[Codes];
    private readonly int[] _index = new int[Indexes + 1];
    private uint _remainder;
    private uint _range = 0xFFFFFFFF;
    private uint _code;

    public ArithmeticSymbols(byte[] data) : base(data) {
      for (var i = 0; i < 5; ++i)
        this._code = (this._code << 8) + this.Get8();
      for (var s = 0; s < this._n; ++s)
        this._live[s] = Total / (uint)this._n + (s < Total - Total / (uint)this._n * (uint)this._n ? 1u : 0u);
      this.Rescale();
    }

    private void Rescale() {
      uint total = 0;
      this._remainder = Total;
      var ind = 0;
      for (var s = 0; s < this._n; ++s) {
        this._cnt[s] = this._live[s];
        this._cum[s] = total;
        total += this._cnt[s];
        var c = this._live[s];
        this._live[s] -= c > 1 && c < 6 ? 1 : c / 6;
        this._remainder -= this._live[s];
        while (ind < Indexes && this._cum[s] + this._cnt[s] - 1 >= Total / Indexes * (uint)ind)
          this._index[ind++] = s;
      }
    }

    private uint Count(int bits) {
      this._range >>= bits;
      if (this._range == 0)
        throw new InvalidDataException("FreeArc tor: range coder underflow.");
      var count = this._code / this._range;
      if (count >= 1u << bits)
        throw new InvalidDataException("FreeArc tor: arithmetic code out of range.");
      return count;
    }

    private void Update(uint cum, uint cnt) {
      this._code -= cum * this._range;
      this._range *= cnt;
      while (this._range < 1u << 24) {
        this._range <<= 8;
        this._code = (this._code << 8) + this.Get8();
      }
    }

    public int Decode() {
      var count = this.Count(RangeBits);
      var s = this._index[count / (Total / Indexes)];
      while (this._cum[s] + this._cnt[s] - 1 < count)
        ++s;
      this.Update(this._cum[s], this._cnt[s]);
      ++this._live[s];
      if (--this._remainder == 0)
        this.Rescale();
      return s;
    }

    public uint GetBits(int n) {
      if (n == 0)
        return 0;
      if (n <= 24) {
        var x = this.Count(n);
        this.Update(x, 1);
        return x;
      }
      var lo = this.Count(15);
      this.Update(lo, 1);
      var hi = this.Count(n - 15);
      this.Update(hi, 1);
      return (hi << 15) + lo;
    }
  }
}
