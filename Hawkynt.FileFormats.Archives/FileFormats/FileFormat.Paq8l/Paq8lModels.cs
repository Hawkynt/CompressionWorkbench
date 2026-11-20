namespace FileFormat.Paq8l;

/// <summary>Longest-match predictor (docs/PAQ8L-FORMAT.md §6.1).</summary>
internal sealed class Paq8lMatchModel {
  private const int MaxLength = 65534;
  private readonly Paq8lHistory _h;
  private readonly int[] _last;
  private readonly Paq8lDirectMap _map;
  private int _hash, _ptr, _len, _result;

  public Paq8lMatchModel(Paq8lHistory h) {
    this._h = h;
    this._last = new int[h.Mem];
    this._map = new Paq8lDirectMap(h, 0x20000);
  }

  public int Mix(Paq8lMixer m) {
    var h = this._h;
    if (h.BitPos == 0) {
      this._hash = (this._hash * 997 * 8 + h.Back(1) + 1) & (this._last.Length - 1);
      if (this._len > 0) {
        ++this._len;
        ++this._ptr;
      } else {
        this._ptr = this._last[this._hash];
        if (this._ptr != 0 && h.Pos - this._ptr < h.Size)
          while (h.Back(this._len + 1) == h.At(this._ptr - this._len - 1) && this._len < MaxLength) ++this._len;
      }
      this._last[this._hash] = h.Pos;
      this._result = this._len;
      this._map.Set((uint)h.Pos);
    }

    if (this._len > MaxLength) this._len = MaxLength;
    int sign;
    var next = h.At(this._ptr);
    if (this._len > 0 && h.Back(1) == h.At(this._ptr - 1) && h.C0 == (next + 256) >> (8 - h.BitPos))
      sign = ((next >> (7 - h.BitPos)) & 1) != 0 ? 1 : -1;
    else
      sign = this._len = 0;
    m.Add(sign * 4 * Paq8lTables.Log[this._len]);
    m.Add(sign * 64 * Math.Min(this._len, 32));
    this._map.Mix(m);
    return this._result;
  }
}

/// <summary>Two-level fax image predictor, 1728-pixel rows (§6.2).</summary>
internal sealed class Paq8lFaxModel {
  private readonly Paq8lHistory _h;
  private readonly byte[] _states = new byte[0x10200];
  private readonly int[] _context = new int[3];
  private readonly Paq8lStateMap[] _maps = [new(), new(), new()];
  private uint _r0, _r1, _r2, _r3;

  public Paq8lFaxModel(Paq8lHistory h) => this._h = h;

  public void Mix(Paq8lMixer m) {
    var h = this._h;
    var bit = h.Bit;
    for (var i = 0; i < 3; ++i) this._states[this._context[i]] = (byte)Paq8lTables.Next(this._states[this._context[i]], bit);
    var shift = 7 - h.BitPos;
    this._r0 += this._r0 + (uint)bit;
    this._r1 += this._r1 + (uint)((h.Back(215) >> shift) & 1);
    this._r2 += this._r2 + (uint)((h.Back(431) >> shift) & 1);
    this._r3 += this._r3 + (uint)((h.Back(647) >> shift) & 1);
    var (r0, r1, r2, r3) = (this._r0, this._r1, this._r2, this._r3);
    this._context[0] = (int)((r0 & 7) | (r1 >> 4 & 0x38) | (r2 >> 3 & 0xC0));
    this._context[1] = (int)(0x100 + ((r0 & 1) | (r1 >> 4 & 0x3E) | (r2 >> 2 & 0x40) | (r3 >> 1 & 0x80)));
    this._context[2] = (int)(0x200 + ((r0 & 0x3F) ^ (r1 & 0x3FFE) ^ (r2 << 2 & 0x7F00) ^ (r3 << 5 & 0xF800)));
    for (var i = 0; i < 3; ++i) m.Add(Paq8lTables.Stretch[this._maps[i].Map(this._states[this._context[i]], bit)]);
  }
}

/// <summary>Words, word pairs and text columns (§6.3).</summary>
internal sealed class Paq8lWordModel {
  private readonly Paq8lHistory _h;
  private readonly Paq8lNibbleMap _map;
  private uint _w0, _w1, _w2, _w3, _w4, _w5, _text;
  private int _previousLine = -3, _line = -2;

  public Paq8lWordModel(Paq8lHistory h) {
    this._h = h;
    this._map = new Paq8lNibbleMap(h, h.Mem * 16, 20);
  }

  public void Mix(Paq8lMixer m) {
    var h = this._h;
    if (h.BitPos == 0) {
      var c = (int)(h.C4 & 255);
      if (c is >= 'A' and <= 'Z') c += 'a' - 'A';
      unchecked {
        if (c is >= 'a' and <= 'z' or >= 128) {
          this._w0 = this._w0 * 263 * 32 + (uint)c;
          this._text = this._text * 997 * 16 + (uint)c;
        } else if (this._w0 != 0) {
          this._w5 = this._w4 * 23;
          this._w4 = this._w3 * 19;
          this._w3 = this._w2 * 17;
          this._w2 = this._w1 * 13;
          this._w1 = this._w0 * 11;
          this._w0 = 0;
        }
        if (c == 10) {
          this._previousLine = this._line;
          this._line = h.Pos - 1;
        }
        var col = (uint)Math.Min(255, h.Pos - this._line);
        var above = (uint)h.At(this._previousLine + (int)col);
        var b1 = (uint)h.Back(1);
        var x = this._w0 * 271 + b1;
        var map = this._map;
        map.Set(x);
        map.Set(this._w0);
        map.Set(x + this._w1);
        map.Set(this._w0 + this._w1 * 31);
        map.Set(x + this._w1 + this._w2 * 29);
        map.Set(this._text & 0xFFFFFF);
        map.Set(this._text & 0xFFFFF);
        map.Set(x + this._w2);
        map.Set(x + this._w3);
        map.Set(x + this._w4);
        map.Set(x + this._w5);
        map.Set(b1 | (uint)h.Back(3) << 8 | (uint)h.Back(5) << 16);
        map.Set((uint)h.Back(2) | (uint)h.Back(4) << 8 | (uint)h.Back(6) << 16);
        map.Set(x + this._w1 + this._w3);
        map.Set(x + this._w2 + this._w3);
        map.Set(col << 16 | b1 << 8 | above);
        map.Set(b1 << 8 | above);
        map.Set(col << 8 | b1);
        map.Set(col);
      }
    }
    this._map.Mix(m);
  }
}

/// <summary>Fixed-length records and distances to the last occurrence (§6.4).</summary>
internal sealed class Paq8lRecordModel {
  private readonly Paq8lHistory _h;
  private readonly int[] _p1 = new int[256], _p2 = new int[256], _p3 = new int[256], _p4 = new int[256];
  private readonly int[] _wordPos = new int[0x10000];
  private readonly Paq8lNibbleMap _a, _b, _c, _d;
  private int _length = 2, _candidate1 = 3, _candidate2 = 4, _count1, _count2;

  public Paq8lRecordModel(Paq8lHistory h) {
    this._h = h;
    this._a = new Paq8lNibbleMap(h, 32768, 3);
    this._b = new Paq8lNibbleMap(h, 32768 / 2, 3);
    this._c = new Paq8lNibbleMap(h, 32768 * 2, 3);
    this._d = new Paq8lNibbleMap(h, h.Mem, 3);
  }

  public void Mix(Paq8lMixer m) {
    var h = this._h;
    if (h.BitPos == 0) {
      var w = (int)(h.C4 & 0xFFFF);
      var c = w & 255;
      var d = w >> 8;
      var r = h.Pos - this._p1[c];
      if (r > 1 && r == this._p1[c] - this._p2[c] && r == this._p2[c] - this._p3[c] && r == this._p3[c] - this._p4[c]
          && (r > 15 || c == h.Back(r * 5 + 1) && c == h.Back(r * 6 + 1))) {
        if (r == this._candidate1) ++this._count1;
        else if (r == this._candidate2) ++this._count2;
        else if (this._count1 > this._count2) { this._candidate2 = r; this._count2 = 1; }
        else { this._candidate1 = r; this._count1 = 1; }
      }
      if (this._count1 > 15 && this._length != this._candidate1) { this._length = this._candidate1; this._count1 = this._count2 = 0; }
      if (this._count2 > 15 && this._length != this._candidate2) { this._length = this._candidate2; this._count1 = this._count2 = 0; }

      var len = this._length;
      var b1 = h.Back(1);
      var distance = Paq8lTables.LongLog((uint)(h.Pos - this._wordPos[w])) >> 2;
      unchecked {
        this._a.Set((uint)(c << 8 | Math.Min(255, h.Pos - this._p1[c]) / 4));
        this._a.Set((uint)(w << 9 | distance));
        this._a.Set((uint)(len | h.Back(len) << 10 | h.Back(len * 2) << 18));
        this._b.Set((uint)(w | len << 8));
        this._b.Set((uint)(d | len << 16));
        this._b.Set((uint)(c | len << 8));
        this._c.Set((uint)(b1 << 8 | Math.Min(255, h.Pos - this._p1[b1])));
        this._c.Set((uint)(b1 << 17 | h.Back(2) << 9 | distance));
        var col = h.Pos % len;
        this._c.Set((uint)(b1 << 8 | h.Back(len)));
        this._d.Set((uint)(len | h.Back(len) << 10 | col << 18));
        this._d.Set((uint)(len | b1 << 10 | col << 18));
        this._d.Set((uint)(col | len << 12));
      }
      this._p4[c] = this._p3[c];
      this._p3[c] = this._p2[c];
      this._p2[c] = this._p1[c];
      this._p1[c] = h.Pos;
      this._wordPos[w] = h.Pos;
    }
    this._a.Mix(m);
    this._b.Mix(m);
    this._c.Mix(m);
    this._d.Mix(m);
  }
}

/// <summary>Sparse and masked byte contexts (§6.5).</summary>
internal sealed class Paq8lSparseModel {
  private readonly Paq8lHistory _h;
  private readonly Paq8lNibbleMap _map;
  private uint _mask;

  public Paq8lSparseModel(Paq8lHistory h) {
    this._h = h;
    this._map = new Paq8lNibbleMap(h, h.Mem * 2, 48);
  }

  public void Mix(Paq8lMixer m, int matchClass, int hits) {
    var h = this._h;
    if (h.BitPos == 0) {
      var c4 = h.C4;
      var map = this._map;
      unchecked {
        map.Set(c4 & 0x00F0F0F0);
        map.Set((c4 & 0xF0F0F0F0) + 1);
        map.Set((c4 & 0x00F8F8F8) + 2);
        map.Set((c4 & 0xF8F8F8F8) + 3);
        map.Set((c4 & 0x00E0E0E0) + 4);
        map.Set((c4 & 0xE0E0E0E0) + 5);
        map.Set((c4 & 0x00F0F0FF) + 6);
        map.Set((uint)matchClass);
        map.Set((uint)hits);
        map.Set(c4 & 0x00FF00FF);
        map.Set(c4 & 0xFF0000FF);
        map.Set((uint)(h.Back(1) | h.Back(5) << 8));
        map.Set((uint)(h.Back(1) | h.Back(6) << 8));
        map.Set((uint)(h.Back(3) | h.Back(6) << 8));
        map.Set((uint)(h.Back(4) | h.Back(8) << 8));
        for (var i = 1; i < 8; ++i) {
          map.Set((uint)(h.Back(i + 1) << 8 | h.Back(i + 2)));
          map.Set((uint)(h.Back(i + 1) << 8 | h.Back(i + 3)));
          map.Set((uint)(matchClass | h.Back(i) << 8));
        }
        this._mask = this._mask << 3 | (uint)ByteClass((int)(c4 & 0xFF));
        map.Set(this._mask);
        map.Set(this._mask << 8 | (uint)h.Back(1));
        map.Set(this._mask << 17 | (uint)h.Back(2) << 8 | (uint)h.Back(3));
        map.Set(this._mask & 0x1FF | (c4 & 0xF0F0F0F0) << 9);
      }
    }
    this._map.Mix(m);
  }

  /// <summary>
  /// The byte's class as the format computes it: 0 for even bytes; for odd bytes 1 letter,
  /// 2 punctuation, 3 white space, else 4 (C locale, so bytes ≥ 128 are none of these).
  /// </summary>
  private static int ByteClass(int c) {
    if ((c & 1) == 0) return 0;
    if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') return 1;
    if (c is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126) return 2;
    if (c is >= 9 and <= 13 or 32) return 3;
    return 4;
  }
}

/// <summary>Distances to the last NUL, space and line break (§6.6).</summary>
internal sealed class Paq8lDistanceModel {
  private readonly Paq8lHistory _h;
  private readonly Paq8lNibbleMap _map;
  private int _nul, _space, _line;

  public Paq8lDistanceModel(Paq8lHistory h) {
    this._h = h;
    this._map = new Paq8lNibbleMap(h, h.Mem, 3);
  }

  public void Mix(Paq8lMixer m) {
    var h = this._h;
    if (h.BitPos == 0) {
      var c = (int)(h.C4 & 0xFF);
      if (c == 0x00) this._nul = h.Pos;
      if (c == 0x20) this._space = h.Pos;
      if (c is 0xFF or '\r' or '\n') this._line = h.Pos;
      this._map.Set((uint)(Math.Min(h.Pos - this._nul, 255) | c << 8));
      this._map.Set((uint)(Math.Min(h.Pos - this._space, 255) | c << 8));
      this._map.Set((uint)(Math.Min(h.Pos - this._line, 255) | (c << 8) + 234567));
    }
    this._map.Mix(m);
  }
}

/// <summary>Uncompressed 24-bit BMP and TIFF images (§6.7).</summary>
internal sealed class Paq8lImageModel {
  private readonly Paq8lHistory _h;
  private readonly Paq8lDirectMap[] _direct;
  private readonly Paq8lNibbleMap _map;
  private int _width, _end;
  private int _tiff;

  public Paq8lImageModel(Paq8lHistory h) {
    this._h = h;
    const int size = 0x20000;
    this._direct = [new(h, size), new(h, size), new(h, size), new(h, size), new(h, size), new(h, size * 2)];
    this._map = new Paq8lNibbleMap(h, h.Mem * 4, 8);
  }

  private uint Le32(int i) =>
    unchecked((uint)(this._h.Back(i) + 256 * this._h.Back(i - 1) + 65536 * this._h.Back(i - 2) + 16777216 * this._h.Back(i - 3)));

  private int Le16(int i) => this._h.Back(i) + 256 * this._h.Back(i - 1);

  public int Mix(Paq8lMixer m) {
    var h = this._h;
    if (h.BitPos == 0 && h.Back(54) == 'B' && h.Back(53) == 'M' && this.Le32(44) == 54 && this.Le32(40) == 40 && this.Le32(24) == 0) {
      this._width = (int)((this.Le32(36) + 3) & unchecked((uint)-4)) * 3;
      var height = (int)this.Le32(32);
      this._end = h.Pos;
      if (this._width < 0x30000 && height < 0x10000) this._end = h.Pos + this._width * height;
      else this._end = h.Pos;
    }

    if (h.BitPos == 0) {
      if (h.C4 == 0x49492A00) this._tiff = h.Pos;
      if (h.Pos - this._tiff == 4 && h.C4 != 0x08000000) this._tiff = 0;
      if (this._tiff != 0 && h.Pos - this._tiff == 200) {
        var entries = this.Le16(h.Pos - this._tiff - 4);
        this._width = 0;
        int bpp = 0, compression = 0, width = 0, height = 0;
        for (var i = this._tiff + 6; i < h.Pos - 12 && --entries > 0; i += 12) {
          var tag = this.Le16(h.Pos - i);
          var format = this.Le16(h.Pos - i - 2);
          var count = (int)this.Le32(h.Pos - i - 4);
          var value = (int)this.Le32(h.Pos - i - 8);
          if (format is 3 or 4 && count == 1) {
            if (tag == 256) width = value;
            if (tag == 257) height = value;
            if (tag == 259) compression = value;
            if (tag == 277) bpp = value;
          }
        }
        if (width > 0 && height > 0 && width * height > 50 && compression == 1 && bpp is 1 or 3) {
          this._end = this._tiff + width * height * bpp;
          this._width = width * bpp;
        }
        if (this._end <= h.Pos) this._tiff = this._width = 0;
      }
    }
    if (h.Pos > this._end) return this._width = 0;

    if (h.BitPos == 0) {
      var w = this._width;
      var color = (uint)(h.Pos % 3);
      var mean = h.Back(3) + h.Back(w - 3) + h.Back(w) + h.Back(w + 3);
      var variance = (Sq(h.Back(3)) + Sq(h.Back(w - 3)) + Sq(h.Back(w)) + Sq(h.Back(w + 3)) - mean * mean / 4) >> 2;
      mean >>= 2;
      var logVariance = Paq8lTables.Log[variance & 0xFFFF];
      var map = this._map;
      uint n = 0;
      map.Set(Paq8lTables.Hash(++n, (uint)(h.Back(3) >> 2), (uint)(h.Back(w) >> 2), color));
      map.Set(Paq8lTables.Hash(++n, (uint)(h.Back(3) >> 2), (uint)(h.Back(1) >> 2), color));
      map.Set(Paq8lTables.Hash(++n, (uint)(h.Back(3) >> 2), (uint)(h.Back(2) >> 2), color));
      map.Set(Paq8lTables.Hash(++n, (uint)(h.Back(w) >> 2), (uint)(h.Back(1) >> 2), color));
      map.Set(Paq8lTables.Hash(++n, (uint)(h.Back(w) >> 2), (uint)(h.Back(2) >> 2), color));
      map.Set(Paq8lTables.Hash(++n, (uint)((h.Back(3) + h.Back(w)) >> 1), color));
      map.Set(Paq8lTables.Hash(++n, (uint)((h.Back(3) + h.Back(w)) >> 3), (uint)(h.Back(1) >> 5), (uint)(h.Back(2) >> 5), color));
      map.Set(Paq8lTables.Hash(++n, (uint)mean, (uint)(logVariance >> 5), color));
      this._direct[0].Set((uint)((h.Back(3) + h.Back(w)) >> 1));
      this._direct[1].Set((uint)((h.Back(3) + h.Back(w) - h.Back(w + 3)) >> 1));
      this._direct[2].Set((uint)((h.Back(3) * 2 - h.Back(6)) >> 1));
      this._direct[3].Set((uint)((h.Back(w) * 2 - h.Back(w * 2)) >> 1));
      this._direct[4].Set((uint)((h.Back(3) + h.Back(w) - h.Back(w - 3)) >> 1));
      this._direct[5].Set((uint)(mean >> 1 | logVariance << 1 & 0x180));
    }
    foreach (var d in this._direct) d.Mix(m);
    this._map.Mix(m);
    return this._width;
  }

  private static int Sq(int x) => x * x;
}

/// <summary>32-bit x86 code: prefixes, opcode and the parse-relevant ModR/M bits (§6.9).</summary>
internal sealed class Paq8lX86Model {
  private readonly Paq8lHistory _h;
  private readonly Paq8lNibbleMap _map;

  public Paq8lX86Model(Paq8lHistory h) {
    this._h = h;
    this._map = new Paq8lNibbleMap(h, h.Mem, 12);
  }

  public void Mix(Paq8lMixer m) {
    var h = this._h;
    if (h.BitPos == 0)
      for (var i = 0; i < 12; ++i) {
        var b2 = h.Back(i + 2);
        var b3 = h.Back(i + 3);
        var prefix = (b2 == 0x0F ? 1 : 0) + 2 * (b2 == 0x66 ? 1 : 0) + 3 * (b2 == 0x67 ? 1 : 0)
          + 4 * (b3 == 0x0F ? 1 : 0) + 8 * (b3 == 0x66 ? 1 : 0) + 12 * (b3 == 0x67 ? 1 : 0);
        var opcode = h.Back(i + 1);
        var modrm = i != 0 ? h.Back(i) & 0xC7 : 0;
        var x = i > 4 ? h.Back(1) : 0;
        this._map.Set((uint)(prefix | opcode << 4 | modrm << 12 | x << 20));
      }
    this._map.Mix(m);
  }
}

/// <summary>Byte histories seen within one- and two-byte contexts (§6.10).</summary>
internal sealed class Paq8lIndirectModel {
  private readonly Paq8lHistory _h;
  private readonly Paq8lNibbleMap _map;
  private readonly uint[] _one = new uint[256];
  private readonly ushort[] _two = new ushort[0x10000];

  public Paq8lIndirectModel(Paq8lHistory h) {
    this._h = h;
    this._map = new Paq8lNibbleMap(h, h.Mem, 6);
  }

  public void Mix(Paq8lMixer m) {
    var h = this._h;
    if (h.BitPos == 0) {
      var d = h.C4 & 0xFFFF;
      var c = d & 255;
      this._one[d >> 8] = this._one[d >> 8] << 8 | c;
      var k = (int)(h.C4 >> 8 & 0xFFFF);
      this._two[k] = (ushort)(this._two[k] << 8 | (int)c);
      var t = c | this._one[c] << 8;
      this._map.Set(t & 0xFFFF);
      this._map.Set(t & 0xFFFFFF);
      this._map.Set(t);
      this._map.Set(t & 0xFF00);
      t = d | (uint)this._two[d] << 16;
      this._map.Set(t & 0xFFFFFF);
      this._map.Set(t);
    }
    this._map.Mix(m);
  }
}

/// <summary>Dynamic Markov model over a cloned bit graph (§6.11).</summary>
internal sealed class Paq8lDmcModel {
  private readonly Paq8lHistory _h;
  private readonly int[] _next0, _next1;
  private readonly byte[] _state;
  private readonly ushort[] _n0, _n1;
  private readonly Paq8lStateMap _map = new();
  private int _top, _current, _threshold = 256;

  public Paq8lDmcModel(Paq8lHistory h) {
    this._h = h;
    var nodes = h.Mem * 2;
    this._next0 = new int[nodes];
    this._next1 = new int[nodes];
    this._state = new byte[nodes];
    this._n0 = new ushort[nodes];
    this._n1 = new ushort[nodes];
  }

  public void Mix(Paq8lMixer m) {
    var h = this._h;
    var bit = h.Bit;
    var size = this._state.Length;

    if (this._top > 0 && this._top < size) {
      var cur = this._current;
      var next = bit != 0 ? this._next1[cur] : this._next0[cur];
      int n = bit != 0 ? this._n1[cur] : this._n0[cur];
      var nn = this._n0[next] + this._n1[next];
      if (n >= this._threshold * 2 && nn - n >= this._threshold * 3) {
        var r = n * 4096 / nn;
        var top = this._top;
        this._n0[top] = (ushort)((this._n0[next] * r >> 12) & 0xFFF);
        this._n0[next] -= this._n0[top];
        this._n1[top] = (ushort)((this._n1[next] * r >> 12) & 0xFFF);
        this._n1[next] -= this._n1[top];
        this._next0[top] = this._next0[next];
        this._next1[top] = this._next1[next];
        this._state[top] = this._state[next];
        if (bit != 0) this._next1[cur] = top; else this._next0[cur] = top;
        ++this._top;
        if (this._top == h.Mem * 2) this._threshold = 512;
        if (this._top == h.Mem * 3) this._threshold = 768;
      }
    }

    if (this._top == size && h.BitPos == 1) this._top = 0;
    if (this._top == 0) {
      for (var i = 0; i < 256; ++i)
        for (var j = 0; j < 256; ++j) {
          var node = j * 256 + i;
          if (i < 127) {
            this._next0[node] = j * 256 + i * 2 + 1;
            this._next1[node] = j * 256 + i * 2 + 2;
          } else {
            this._next0[node] = (i - 127) * 256;
            this._next1[node] = (i + 1) * 256;
          }
          this._n0[node] = 128;
          this._n1[node] = 128;
        }
      this._top = 65536;
      this._current = 0;
      this._threshold = 256;
    }

    var c = this._current;
    if (bit != 0) {
      if (this._n1[c] < 3800) this._n1[c] += 256;
    } else if (this._n0[c] < 3800) this._n0[c] += 256;
    this._state[c] = (byte)Paq8lTables.Next(this._state[c], bit);
    this._current = c = bit != 0 ? this._next1[c] : this._next0[c];

    var p1 = this._map.Map(this._state[c], bit);
    int ones = this._n1[c], zeros = this._n0[c];
    var p2 = (ones + 5) * 4096 / (zeros + ones + 10);
    m.Add(Paq8lTables.Stretch[p1]);
    m.Add(Paq8lTables.Stretch[p2]);
  }
}
