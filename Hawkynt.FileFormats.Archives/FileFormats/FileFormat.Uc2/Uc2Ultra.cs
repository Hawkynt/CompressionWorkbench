namespace FileFormat.Uc2;

/// <summary>
/// UltraCompressor II's "Ultra" bitstream (methods 1-9, 21-29, 30-37, 40-47): LZ77 over a 64 KiB
/// history that starts with a dictionary ("master"), Huffman-coded with a 316-symbol
/// literal/distance alphabet and a 28-symbol length alphabet, trees either the built-in defaults
/// or sent delta-coded against the previous block's. Bits are read most-significant first out of
/// little-endian 16-bit words.
/// </summary>
/// <remarks>
/// Converted from klauspost/uc2 (internal/ultra, LGPL-3.0), which follows the published UC2 r2
/// source; the default code lengths and the delta-code matrices are the format's own constants.
/// </remarks>
internal static class Uc2Ultra {
  private const int NumLiterals = 256;
  private const int NumLd = 316;
  private const int NumL = 28;
  private const int NumSymbols = NumLd + NumL;
  private const int NumPre = 15;
  private const int RepeatSymbol = 14;
  private const int MinRepeat = 6;
  private const int MaxCodeBits = 13;
  private const int EobDistance = 64001;

  /// <summary>Largest distance UC2's own encoder emits.</summary>
  public const int MaxEncoderDistance = 64000;

  /// <summary>Default ("base") code lengths, TREEENC.CPP BasePrev.</summary>
  internal static readonly byte[] BaseLengths = BuildBaseLengths();

  private static byte[] BuildBaseLengths() {
    var b = new byte[NumSymbols];
    void Fill(int lo, int hi, byte v) { for (var i = lo; i < hi; ++i) b[i] = v; }
    Fill(0, 32, 9);
    b[10] = b[12] = b[32] = 7;
    Fill(33, 128, 8);
    b[46] = b[58] = b[92] = 7;
    Fill(128, 256, 10);
    Fill(256, 272, 6);
    Fill(272, 284, 7);
    Fill(284, 290, 8);
    Fill(290, 300, 9);
    Fill(300, 316, 10);
    Fill(316, 325, 4);
    Fill(325, 334, 5);
    Fill(334, 344, 6);
    return b;
  }

  /// <summary>vval[prev][code] → new length (decoder side of the delta matrices, TREEENC.CPP InitTables).</summary>
  private static readonly byte[,] DeltaDecode = BuildDeltaDecode();

  private static byte[,] BuildDeltaDecode() {
    const int n = 14;
    var table = new byte[n, n];
    var vval = new byte[n, n];
    for (var i = 1; i < n; ++i) { vval[0, i] = (byte)(n - i); table[0, i] = (byte)(n - i); }
    for (var i = 1; i < n; ++i) {
      var prob = 0;
      var x = i < 9 ? i - 1 : n - i + 1;
      var j = 1;
      for (; j < x; ++j) {
        table[i, i + j - 1] = (byte)prob;
        vval[i, prob] = (byte)(i + j - 1);
        ++prob;
        table[i, i - j] = (byte)prob;
        vval[i, prob] = (byte)(i - j);
        ++prob;
      }
      table[i, i - j] = (byte)(prob + 1);
      vval[i, prob + 1] = (byte)(i - j);
      if (i < 8) {
        table[i, 0] = n - 1;
        vval[i, n - 1] = 0;
        table[i, i + j - 1] = (byte)prob;
        vval[i, prob] = (byte)(i + j - 1);
        for (j = n - 1; j >= i * 2 - 1; --j) {
          table[i, j] = (byte)(j - 1);
          vval[i, j - 1] = (byte)j;
        }
      } else {
        table[i, 0] = (byte)prob;
        vval[i, prob] = 0;
        for (j = 1; j <= i * 2 - 15; ++j) {
          table[i, j] = (byte)(n - j);
          vval[i, n - j] = (byte)j;
        }
      }
    }
    return vval;
  }

  private static readonly ushort[] DistBase = new ushort[60];
  private static readonly byte[] DistExtra = new byte[60];
  private static readonly ushort[] LenBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 21, 23, 25, 27, 35, 43, 51, 59, 67, 75, 83, 91, 155, 667, 2715];
  private static readonly byte[] LenExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 6, 9, 11, 15];

  static Uc2Ultra() {
    for (var i = 0; i < 15; ++i) {
      DistBase[i] = (ushort)(i + 1);
      DistBase[15 + i] = (ushort)((i + 1) << 4); DistExtra[15 + i] = 4;
      DistBase[30 + i] = (ushort)((i + 1) << 8); DistExtra[30 + i] = 8;
      DistBase[45 + i] = (ushort)((i + 1) << 12); DistExtra[45 + i] = 12;
    }
  }

  // ── bit reader ──────────────────────────────────────────────────────

  /// <summary>Reads MSB-first out of little-endian 16-bit words; past the end it supplies zero bits.</summary>
  private sealed class BitReader(byte[] data, int offset, int length) {
    private long _bit; // bits consumed

    /// <summary>More than a word of zero padding has been consumed: the stream is truncated.</summary>
    public bool Overrun => this._bit > (long)length * 8 + 16;

    public int Bit() {
      var word = this._bit >> 4;
      var inWord = (int)(this._bit & 15);
      var byteIndex = word * 2 + (inWord < 8 ? 1 : 0);
      var b = byteIndex < length ? data[offset + byteIndex] : 0;
      ++this._bit;
      return (b >> (7 - (inWord & 7))) & 1;
    }

    public int Bits(int n) {
      var v = 0;
      for (var i = 0; i < n; ++i) v = (v << 1) | this.Bit();
      return v;
    }
  }

  // ── canonical code ──────────────────────────────────────────────────

  private sealed class Code {
    // first-code / count tables per length for canonical decoding (DEFLATE style, MSB first)
    private readonly int[] _count = new int[MaxCodeBits + 1];
    private readonly int[] _symbols;
    private readonly int _maxLen;

    public Code(ReadOnlySpan<byte> lengths, int maxLen) {
      this._maxLen = maxLen;
      var n = 0;
      foreach (var l in lengths) {
        if (l > maxLen) throw new InvalidDataException("UC2 code length exceeds the format maximum.");
        if (l > 0) { ++this._count[l]; ++n; }
      }
      // reject oversubscribed codes; incomplete ones are legal
      var left = 1;
      for (var len = 1; len <= maxLen; ++len) {
        left = (left << 1) - this._count[len];
        if (left < 0) throw new InvalidDataException("UC2 Huffman code is oversubscribed.");
      }
      this._symbols = new int[n];
      var offs = new int[maxLen + 2];
      for (var len = 1; len <= maxLen; ++len) offs[len + 1] = offs[len] + this._count[len];
      for (var s = 0; s < lengths.Length; ++s)
        if (lengths[s] > 0) this._symbols[offs[lengths[s]]++] = s;
    }

    public int Decode(BitReader r) {
      int code = 0, first = 0, index = 0;
      for (var len = 1; len <= this._maxLen; ++len) {
        code |= r.Bit();
        var count = this._count[len];
        if (code - first < count) return this._symbols[index + code - first];
        index += count;
        first += count;
        first <<= 1;
        code <<= 1;
      }
      throw new InvalidDataException("UC2 stream uses an unassigned Huffman code.");
    }
  }

  private static readonly Lazy<(Code Ld, Code L)> BaseCodes =
    new(() => (new Code(BaseLengths.AsSpan(0, NumLd), MaxCodeBits), new Code(BaseLengths.AsSpan(NumLd), MaxCodeBits)));

  /// <summary>
  /// Decodes one Ultra stream. <paramref name="outputLength"/> &lt; 0 decodes to the stream's
  /// end-of-stream bit (the CDIR); otherwise decoding stops after that many bytes.
  /// </summary>
  public static byte[] Decode(byte[] input, int offset, int length, ReadOnlySpan<byte> dictionary, long outputLength, long cap = 100_000_000) {
    if (outputLength == 0) return [];
    var limit = outputLength < 0 ? cap : outputLength;
    if (limit > int.MaxValue - 65536) throw new NotSupportedException("UC2 member exceeds the 2 GiB in-memory limit.");
    var dictLen = dictionary.Length;
    var window = new byte[dictLen + (int)Math.Min(limit, 1 << 20)];
    dictionary.CopyTo(window);
    var w = dictLen;
    var end = (long)dictLen + limit;
    var r = new BitReader(input, offset, length);
    var prev = (byte[])BaseLengths.Clone();
    Code? ld = null, l = null;

    while (w < end) {
      if (r.Bit() == 0) break; // end of stream
      (ld, l) = ReadTrees(r, prev);
      while (w < end) {
        if (r.Overrun) throw new InvalidDataException("UC2 stream ended early.");
        var sym = ld.Decode(r);
        if (w + 35482 >= window.Length && window.Length < end)
          Array.Resize(ref window, (int)Math.Min(end, Math.Max(window.Length * 2L, w + 65536L)));
        if (sym < NumLiterals) {
          window[w++] = (byte)sym;
          continue;
        }
        sym -= NumLiterals;
        var dist = DistBase[sym] + r.Bits(DistExtra[sym]);
        var ls = l.Decode(r);
        if (dist == EobDistance) break; // end of block: the length symbol's extra bits are not sent
        var len = LenBase[ls] + r.Bits(LenExtra[ls]);
        if (dist > w) throw new InvalidDataException("UC2 match reaches before the dictionary.");
        if (len > end - w) len = (int)(end - w);
        var src = w - dist;
        for (var k = 0; k < len; ++k) window[w++] = window[src + k];
      }
    }
    if (outputLength >= 0 && w < end)
      throw new InvalidDataException("UC2 stream ended before the recorded size.");
    return window.AsSpan(dictLen, w - dictLen).ToArray();
  }

  private static (Code Ld, Code L) ReadTrees(BitReader r, byte[] prev) {
    if (r.Bit() == 0) {
      BaseLengths.CopyTo(prev, 0);
      return BaseCodes.Value;
    }
    var t = r.Bits(2);
    var total = NumSymbols - ((t & 1) == 0 ? 28 : 0) - ((t & 2) == 0 ? 128 : 0);
    var preLengths = new byte[NumPre];
    for (var i = 0; i < NumPre; ++i) preLengths[i] = (byte)r.Bits(3);
    var pre = new Code(preLengths, 7);
    var st = new byte[NumSymbols + 20];
    int count = 0;
    byte val = 0;
    while (count < total) {
      var s = pre.Decode(r);
      if (s != RepeatSymbol) {
        val = (byte)s;
        st[count++] = val;
        continue;
      }
      var c = pre.Decode(r);
      for (var k = 0; k < c + MinRepeat - 1 && count < st.Length; ++k) st[count++] = val;
    }
    var lens = new byte[NumSymbols];
    var idx = 0;
    void Put(int lo, int hi) { for (var s = lo; s < hi; ++s) lens[s] = DeltaDecode[prev[s], st[idx++]]; }
    if ((t & 1) != 0) Put(0, 32);
    else { Put(9, 11); Put(12, 14); }
    Put(32, 128);
    if ((t & 2) != 0) Put(128, 256);
    Put(256, NumSymbols);
    lens.CopyTo(prev, 0);
    return (new Code(lens.AsSpan(0, NumLd), MaxCodeBits), new Code(lens.AsSpan(NumLd), MaxCodeBits));
  }

  // ── encoder (default trees, greedy hash-chain matcher) ───────────────

  private sealed class BitWriter {
    private readonly List<byte> _out = [];
    private uint _acc;
    private int _n;

    public void Put(int value, int bits) {
      for (var i = bits - 1; i >= 0; --i) {
        this._acc = (this._acc << 1) | (uint)((value >> i) & 1);
        if (++this._n == 16) {
          this._out.Add((byte)this._acc);
          this._out.Add((byte)(this._acc >> 8));
          this._acc = 0;
          this._n = 0;
        }
      }
    }

    public byte[] Finish() {
      if (this._n > 0) this.Put(0, 16 - this._n);
      return [.. this._out];
    }
  }

  private static (int[] Codes, byte[] Lens) CanonicalCodes(ReadOnlySpan<byte> lengths) {
    var codes = new int[lengths.Length];
    var count = new int[MaxCodeBits + 2];
    foreach (var l in lengths) if (l > 0) ++count[l];
    var next = new int[MaxCodeBits + 2];
    var code = 0;
    for (var len = 1; len <= MaxCodeBits; ++len) {
      code = (code + count[len - 1]) << 1;
      next[len] = code;
    }
    for (var s = 0; s < lengths.Length; ++s)
      if (lengths[s] > 0) codes[s] = next[lengths[s]]++;
    return (codes, lengths.ToArray());
  }

  private static readonly Lazy<(int[] Codes, byte[] Lens)> BaseLdCodes = new(() => CanonicalCodes(BaseLengths.AsSpan(0, NumLd)));
  private static readonly Lazy<(int[] Codes, byte[] Lens)> BaseLCodes = new(() => CanonicalCodes(BaseLengths.AsSpan(NumLd)));

  /// <summary>
  /// Encodes <paramref name="data"/> as one Ultra stream using the default trees, with
  /// <paramref name="dictionary"/> as history. Matches stay within distance 64000 and length 32760.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, ReadOnlySpan<byte> dictionary) {
    var w = new BitWriter();
    if (data.IsEmpty) { w.Put(0, 1); return w.Finish(); }

    var (ldc, ldl) = BaseLdCodes.Value;
    var (lc, ll) = BaseLCodes.Value;
    var buf = new byte[dictionary.Length + data.Length];
    dictionary.CopyTo(buf);
    data.CopyTo(buf.AsSpan(dictionary.Length));
    var start = dictionary.Length;

    const int hashBits = 15;
    var head = new int[1 << hashBits];
    Array.Fill(head, -1);
    var chain = new int[buf.Length];
    int Hash(int p) => ((buf[p] << 10) ^ (buf[p + 1] << 5) ^ buf[p + 2]) & ((1 << hashBits) - 1);
    void Insert(int p) { if (p + 2 < buf.Length) { var h = Hash(p); chain[p] = head[h]; head[h] = p; } }
    for (var p = 0; p < start; ++p) Insert(p);

    w.Put(1, 1); // block
    w.Put(0, 1); // default trees
    var pos = start;
    const int maxChain = 64, maxLen = 32760;
    while (pos < buf.Length) {
      int bestLen = 0, bestDist = 0;
      if (pos + 2 < buf.Length) {
        var cand = head[Hash(pos)];
        for (var steps = 0; cand >= 0 && steps < maxChain; ++steps, cand = chain[cand]) {
          var d = pos - cand;
          if (d > MaxEncoderDistance) break;
          if (d == EobDistance) continue;
          var limit = Math.Min(maxLen, buf.Length - pos);
          var l = 0;
          while (l < limit && buf[cand + l] == buf[pos + l]) ++l;
          if (l > bestLen) { bestLen = l; bestDist = d; if (l == limit) break; }
        }
      }
      if (bestLen >= 3) {
        EmitDistance(w, ldc, ldl, bestDist);
        EmitLength(w, lc, ll, bestLen);
        for (var k = 0; k < bestLen; ++k) Insert(pos + k);
        pos += bestLen;
      } else {
        w.Put(ldc[buf[pos]], ldl[buf[pos]]);
        Insert(pos);
        ++pos;
      }
    }
    // end of block: distance 64001 then one length code (L0) without extra bits
    EmitDistance(w, ldc, ldl, EobDistance);
    w.Put(lc[0], ll[0]);
    w.Put(0, 1); // end of stream
    return w.Finish();
  }

  private static void EmitDistance(BitWriter w, int[] codes, byte[] lens, int d) {
    int sym, extra, bits;
    if (d < 16) { sym = 255 + d; extra = 0; bits = 0; }
    else if (d < 256) { sym = 270 + (d >> 4); extra = d & 15; bits = 4; }
    else if (d < 4096) { sym = 285 + (d >> 8); extra = d & 255; bits = 8; }
    else { sym = 300 + (d >> 12); extra = d & 4095; bits = 12; }
    w.Put(codes[sym], lens[sym]);
    if (bits > 0) w.Put(extra, bits);
  }

  private static void EmitLength(BitWriter w, int[] codes, byte[] lens, int n) {
    int sym, extra, bits;
    if (n < 11) { sym = n - 3; extra = 0; bits = 0; }
    else if (n < 27) { sym = 8 + (n - 11) / 2; extra = (n - 11) & 1; bits = 1; }
    else if (n < 91) { sym = 16 + (n - 27) / 8; extra = (n - 27) & 7; bits = 3; }
    else if (n < 155) { sym = 24; extra = n - 91; bits = 6; }
    else if (n < 667) { sym = 25; extra = n - 155; bits = 9; }
    else if (n < 2715) { sym = 26; extra = n - 667; bits = 11; }
    else { sym = 27; extra = n - 2715; bits = 15; }
    w.Put(codes[sym], lens[sym]);
    if (bits > 0) w.Put(extra, bits);
  }
}
