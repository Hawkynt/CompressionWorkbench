namespace FileFormat.Paq8l;

/// <summary>
/// Baseline Huffman JPEG: follows the entropy-coded data closely enough to know which code and
/// coefficient is being coded, and predicts with a model of its own (docs/PAQ8L-FORMAT.md §6.8).
/// </summary>
internal sealed class Paq8lJpegModel {
  private const int Contexts = 19;
  private static readonly byte[] ZigzagU = [
    0, 1, 0, 0, 1, 2, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 7, 6, 5, 4,
    3, 2, 1, 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 5, 6, 7, 7, 6, 7,
  ];
  private static readonly byte[] ZigzagV = [
    0, 0, 1, 2, 1, 0, 0, 1, 2, 3, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3,
    4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 4, 5, 6, 7, 7, 6, 5, 6, 7, 7,
  ];

  private readonly Paq8lHistory _h;

  // parser
  private int _jpeg, _next, _skip, _sof, _sos, _data, _tableCount;
  private readonly int[] _tables = new int[8];

  // Huffman decoding
  private uint _code;
  private int _bits, _size, _rs = -1, _mcuPos, _mcuSize;
  private readonly uint[] _min = new uint[128], _max = new uint[128];
  private readonly int[] _val = new int[128];
  private readonly byte[] _values = new byte[2048];
  private readonly int[,] _select = new int[2, 10];
  private readonly int[] _color = new int[10];
  private readonly int[] _dcPrediction = new int[4];
  private int _dc, _width, _row, _column;
  private readonly byte[] _coefficients = new byte[0x20000];
  private int _cpos;
  private uint _hash1, _hash2, _hash3, _hash4;
  private int _rs1, _rs2, _rs3, _rs4;
  private int _sum, _sum1, _sum2, _sum3, _sum4;

  // own model, created when the first scan starts
  private Paq8lBucketTable? _table;
  private readonly uint[] _context = new uint[Contexts];
  private readonly int[] _ptr = new int[Contexts];
  private readonly Paq8lStateMap[] _maps = new Paq8lStateMap[Contexts];
  private Paq8lMixer? _mixer;
  private Paq8lApm? _apm1, _apm2;
  private int _hbCount = 2;
  private bool _pointersSet;

  public Paq8lJpegModel(Paq8lHistory h) => this._h = h;

  private int Coefficient(int at) => this._coefficients[at & 0x1FFFF];

  private void Put(int value) => this._coefficients[this._cpos++ & 0x1FFFF] = (byte)value;

  private int GiveUp() {
    this._jpeg = 0;
    return this._next;
  }

  /// <summary>Adds this bit's input when inside JPEG scan data; returns non-zero then.</summary>
  public int Mix(Paq8lMixer m) {
    var h = this._h;
    var bpos = h.BitPos;
    if (bpos == 0) this._next = this._jpeg > 1 ? 1 : 0;
    if (bpos != 0 && this._jpeg == 0) return this._next;
    if (bpos == 0 && this._skip > 0) --this._skip;
    if (this._skip > 0) return this._next;

    if (bpos == 0) {
      if (this._jpeg == 0 && h.Back(4) == 0xFF && h.Back(3) == 0xD8 && h.Back(2) == 0xFF && h.Back(1) >> 4 == 0xE) {
        this._jpeg = 1;
        this._skip = this._sos = this._sof = this._tableCount = this._data = this._mcuSize = 0;
        this._code = 0;
        this._bits = this._size = this._mcuPos = this._cpos = 0;
        this._rs = -1;
        Array.Clear(this._min);
        Array.Clear(this._max);
        Array.Clear(this._val);
        Array.Clear(this._dcPrediction);
      }
      if (this._jpeg != 0 && this._data != 0 && h.Back(2) == 0xFF && h.Back(1) != 0 && (h.Back(1) & 0xF8) != 0xD0) {
        if (h.Back(1) != 0xD9) return this.GiveUp();
        this._jpeg = 0;
      }
      if (this._jpeg == 0) return this._next;

      if (this._data == 0 && this._skip == 0 && h.Back(4) == 0xFF && (h.Back(3) >> 4 == 0xE || h.Back(3) == 0xFE))
        this._skip = h.Back(2) * 256 + h.Back(1) + 2;
      if (h.Back(5) == 0xFF && h.Back(4) == 0xDA) {
        var len = h.Back(3) * 256 + h.Back(2);
        if (len == 6 + 2 * h.Back(1) && h.Back(1) != 0 && h.Back(1) <= 4) {
          this._sos = h.Pos - 5;
          this._data = this._sos + len + 2;
          this._jpeg = 2;
        }
      }
      if (h.Back(4) == 0xFF && h.Back(3) == 0xC4 && this._tableCount < 8) this._tables[this._tableCount++] = h.Pos - 4;
      if (h.Back(4) == 0xFF && h.Back(3) == 0xC0) this._sof = h.Pos - 4;
      if (h.Back(2) == 0xFF && (h.Back(1) & 0xF8) == 0xD0) {
        this._code = 0;
        this._bits = this._size = this._mcuPos = 0;
        this._rs = -1;
        Array.Clear(this._dcPrediction);
      }
    }

    if (h.Pos == this._data && bpos == 1) {
      var r = this.BuildTables();
      if (r != null) return r.Value;
    }

    if (this._mcuSize != 0 && h.Back(1 + (bpos == 0 ? 1 : 0)) != 0xFF) {
      var r = this.DecodeStep(h.Bit);
      if (r != null) return r.Value;
    }

    if (this._jpeg == 0 || this._data == 0) return this._next;
    this.Predict(m);
    return 1;
  }

  /// <summary>Reads the DHT segments and the frame and scan headers; non-null to return early.</summary>
  private int? BuildTables() {
    var h = this._h;
    if (this._tableCount <= 0) return this.GiveUp();
    for (var i = 0; i < this._tableCount; ++i) {
      var p = this._tables[i] + 4;
      var end = p + h.At(p - 2) * 256 + h.At(p - 1) - 2;
      var count = 0;
      while (p < end && end < h.Pos && end < p + 2100 && ++count < 10) {
        var tc = h.At(p) >> 4;
        var th = h.At(p) & 15;
        if (tc >= 2 || th >= 4) break;
        var entry = tc * 64 + th * 16;
        var val = p + 17;
        var hval = tc * 1024 + th * 256;
        for (var j = 0; j < 256; ++j) this._values[hval + j] = (byte)h.At(val + j);
        uint code = 0;
        for (var j = 0; j < 16; ++j) {
          var lengthCount = h.At(p + j + 1);
          this._min[entry + j] = code;
          code += (uint)lengthCount;
          this._max[entry + j] = code;
          this._val[entry + j] = hval;
          val += lengthCount;
          hval += lengthCount;
          code *= 2;
        }
        p = val;
        if (hval is < 0 or >= 2048) return this.GiveUp();
      }
      if (p != end) return this.GiveUp();
    }
    this._code = 0;
    this._bits = this._size = 0;
    this._rs = -1;

    if (this._sof == 0 && this._sos != 0) return this._next;
    var ns = h.At(this._sos + 4);
    var nf = h.At(this._sof + 9);
    if (ns > 4 || nf > 4) return this.GiveUp();
    this._mcuSize = 0;
    var hmax = 0;
    for (var i = 0; i < ns; ++i)
      for (var j = 0; j < nf; ++j) {
        if (h.At(this._sos + 2 * i + 5) != h.At(this._sof + 3 * j + 10)) continue;
        var hv = h.At(this._sof + 3 * j + 11);
        if (hv >> 4 > hmax) hmax = hv >> 4;
        hv = (hv & 15) * (hv >> 4);
        if (hv < 1 || hv + this._mcuSize > 10) return this.GiveUp();
        while (hv != 0) {
          if (this._mcuSize >= 10) return this.GiveUp();
          var tables = h.At(this._sos + 2 * i + 6);
          this._select[0, this._mcuSize] = tables >> 4 & 15;
          this._select[1, this._mcuSize] = tables & 15;
          if (this._select[0, this._mcuSize] >= 4 || this._select[1, this._mcuSize] >= 4) return this.GiveUp();
          this._color[this._mcuSize] = i;
          --hv;
          ++this._mcuSize;
        }
      }
    if (hmax is < 1 or > 10) return this.GiveUp();
    this._width = h.At(this._sof + 7) * 256 + h.At(this._sof + 8);
    this._width = (this._width - 1) / (hmax * 8) + 1;
    if (this._width <= 0) return this.GiveUp();
    this._mcuSize *= 64;
    this._row = this._column = 0;
    return null;
  }

  /// <summary>Takes one bit of entropy-coded data; non-null to return early.</summary>
  private int? DecodeStep(int bit) {
    if (this._bits > 32) return this.GiveUp();
    this._code += this._code + (uint)bit;
    ++this._bits;
    if (this._rs < 0) {
      if (this._bits is < 1 or > 16) return this.GiveUp();
      var ac = (this._mcuPos & 63) > 0 ? 1 : 0;
      if (this._mcuPos < 0 || this._mcuPos >> 6 >= 10) return this.GiveUp();
      var sel = this._select[ac, this._mcuPos >> 6];
      if (sel is < 0 or >= 4) return this.GiveUp();
      var e = ac * 64 + sel * 16 + this._bits - 1;
      if (this._min[e] > this._max[e] || this._val[e] >= 2048) return this.GiveUp();
      if (this._code < this._max[e]) {
        if (this._code < this._min[e]) return this.GiveUp();
        var k = (int)(this._val[e] + this._code - this._min[e]);
        if (k is < 0 or >= 2048) return this.GiveUp();
        this._rs = this._values[k];
        this._size = this._bits;
      }
    }
    if (this._rs >= 0 && this._size + (this._rs & 15) == this._bits) {
      this._hash4 = this._hash3;
      this._hash3 = this._hash2;
      this._hash2 = this._hash1;
      this._hash1 = Paq8lTables.Hash(this._code, (uint)this._bits);
      this._rs4 = this._rs3;
      this._rs3 = this._rs2;
      this._rs2 = this._rs1;
      this._rs1 = this._rs;
      if ((this._mcuPos & 63) != 0) {
        if (this._rs == 0) {
          this._mcuPos = (this._mcuPos + 63) & -64;
          if (this._mcuPos < 0 || this._mcuPos > this._mcuSize || this._mcuPos > 640) return this.GiveUp();
          while ((this._cpos & 63) != 0) this.Put(0);
        } else {
          if ((this._rs & 15) > 10) return this.GiveUp();
          var r = this._rs >> 4;
          var s = this._rs & 15;
          if (this._mcuPos >> 6 != (this._mcuPos + r) >> 6) return this.GiveUp();
          this._mcuPos += r + 1;
          for (var i = r; i >= 1; --i) this.Put(i << 4 | s);
          this.Put((int)((uint)s << 4 | (this._code << 2 >> s) & 3 | 12));
          this._sum += s;
        }
      } else {
        if (this._rs >= 12) return this.GiveUp();
        ++this._mcuPos;
        var x = (int)(this._code & (uint)((1 << this._rs) - 1));
        if (this._rs != 0 && x >> (this._rs - 1) == 0) x -= (1 << this._rs) - 1;
        if (this._mcuPos < 0 || this._mcuPos >> 6 >= 10) return this.GiveUp();
        var comp = this._color[this._mcuPos >> 6];
        if (comp is < 0 or >= 4) return this.GiveUp();
        this._dc = this._dcPrediction[comp] += x;
        if ((this._cpos & 63) != 0) return this.GiveUp();
        this.Put((this._dc + 1023) >> 3);
        this._sum4 = this._sum3;
        this._sum3 = this._sum2;
        this._sum2 = this._sum1;
        this._sum1 = this._sum;
        this._sum = this._rs;
      }
      if (this._mcuPos < 0 || this._mcuPos > this._mcuSize) return this.GiveUp();
      if (this._mcuPos >= this._mcuSize) {
        this._mcuPos = 0;
        if (++this._column == this._width) {
          this._column = 0;
          ++this._row;
        }
      }
      this._code = 0;
      this._size = this._bits = 0;
      this._rs = -1;
    }
    return null;
  }

  private void Predict(Paq8lMixer m) {
    var h = this._h;
    var bit = h.Bit;
    if (this._table == null) {
      this._table = new Paq8lBucketTable(h.Mem, 9);
      for (var i = 0; i < Contexts; ++i) this._maps[i] = new();
      this._mixer = new Paq8lMixer(32, 800, 4);
      this._apm1 = new Paq8lApm(1024);
      this._apm2 = new Paq8lApm(0x10000);
    }
    var t = this._table.Bytes;
    var mixer = this._mixer!;

    if (this._pointersSet)
      for (var i = 0; i < Contexts; ++i) t[this._ptr[i]] = (byte)Paq8lTables.Next(t[this._ptr[i]], bit);
    mixer.Train(bit);

    var mcuPos = this._mcuPos;
    var comp = this._color[mcuPos >> 6];
    var coef = (mcuPos & 63) | comp << 6;
    var hc = this._code | 1u << this._bits;
    if (++this._hbCount > 2 || this._bits == 0) this._hbCount = 0;
    var zu = ZigzagU[mcuPos & 63];
    var zv = ZigzagV[mcuPos & 63];
    if (this._hbCount == 0) {
      var mpos = mcuPos >> 4 | ((mcuPos & -64) == 0 ? 1 : 0) << 7;
      var c = this._context;
      uint n = 0;
      c[0] = Paq8lTables.Hash(++n, hc, (uint)(mcuPos >> 2), (uint)Math.Min(3, mcuPos & 63));
      c[1] = Paq8lTables.Hash(++n, hc, (uint)(mpos >> 4), (uint)this.Coefficient(this._cpos - this._mcuSize));
      c[2] = Paq8lTables.Hash(++n, hc, (uint)(mpos >> 4), (uint)this.Coefficient(this._cpos - this._width * this._mcuSize));
      c[3] = Paq8lTables.Hash(++n, hc, Paq8lTables.Log[this._sum3 & 0xFFFF], (uint)coef);
      c[4] = Paq8lTables.Hash(++n, hc, (uint)coef, (uint)(this._column >> 3));
      c[5] = Paq8lTables.Hash(++n, hc, (uint)coef, (uint)(this._column >> 1));
      c[6] = Paq8lTables.Hash(++n, hc, (uint)this._rs1, (uint)mpos);
      c[7] = Paq8lTables.Hash(++n, hc, (uint)this._rs1, (uint)this._rs2);
      c[8] = Paq8lTables.Hash(++n, hc, (uint)this._rs1, (uint)this._rs2, (uint)this._rs3);
      c[9] = Paq8lTables.Hash(++n, hc, (uint)(this._sum >> 4), (uint)mcuPos);
      c[10] = Paq8lTables.Hash(++n, hc, (uint)mpos, (uint)this.Coefficient(this._cpos - 1));
      c[11] = Paq8lTables.Hash(++n, hc, (uint)this._dc);
      c[12] = Paq8lTables.Hash(++n, hc, (uint)this._rs1, (uint)coef);
      c[13] = Paq8lTables.Hash(++n, hc, (uint)this._rs1, (uint)this._rs2, (uint)coef);
      c[14] = Paq8lTables.Hash(++n, hc, (uint)(mcuPos >> 3), (uint)(this._sum3 >> 3));
      c[15] = Paq8lTables.Hash(++n, hc, this._hash1);
      c[16] = Paq8lTables.Hash(++n, hc, (uint)coef, this._hash1);
      c[17] = Paq8lTables.Hash(++n, hc, zu, (uint)comp);
      c[18] = Paq8lTables.Hash(++n, hc, zv, (uint)comp);
    }

    mixer.Add(128);
    for (var i = 0; i < Contexts; ++i) {
      if (this._hbCount == 0) this._ptr[i] = this._table.Find(this._context[i]) + 1;
      else if (this._hbCount == 1) this._ptr[i] += 1 + (int)(this._code & 1) * 3;
      else this._ptr[i] += 1 + (int)(this._code & 1);
      mixer.Add(Paq8lTables.Stretch[this._maps[i].Map(t[this._ptr[i]], bit)]);
    }
    this._pointersSet = true;
    mixer.Select(0, 1);
    mixer.Select(coef, 64);
    mixer.Select(mcuPos, 640);
    var pr = mixer.Predict(bit);
    pr = this._apm1!.Refine(pr, (int)(hc & 1023), bit);
    pr = this._apm2!.Refine(pr, (int)(hc & 255) | coef << 8, bit);
    m.Add(Paq8lTables.Stretch[pr]);
  }
}
