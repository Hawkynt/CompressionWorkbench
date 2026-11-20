namespace FileFormat.Paq8l;

/// <summary>
/// The complete paq8l predictor: every model, the main mixer and the final APM stage
/// (docs/PAQ8L-FORMAT.md §6.12 and §7). One instance carries its state across all files of an
/// archive.
/// </summary>
internal sealed class Paq8lPredictor {
  private readonly Paq8lHistory _h;
  private readonly Paq8lNibbleMap _orders;
  private readonly Paq8lRunMap _run1, _run2, _run3;
  private readonly Paq8lMixer _mixer;
  private readonly uint[] _orderHash = new uint[16];
  private readonly Paq8lJpegModel _jpeg;
  private readonly Paq8lMatchModel _match;
  private readonly Paq8lImageModel _image;
  private readonly Paq8lSparseModel? _sparse;
  private readonly Paq8lDistanceModel? _distance;
  private readonly Paq8lFaxModel? _fax;
  private readonly Paq8lRecordModel? _record;
  private readonly Paq8lWordModel? _word;
  private readonly Paq8lIndirectModel? _indirect;
  private readonly Paq8lDmcModel? _dmc;
  private readonly Paq8lX86Model? _x86;
  private readonly Paq8lApm _a = new(256);
  private readonly Paq8lApm _a1 = new(0x10000), _a2 = new(0x10000), _a3 = new(0x10000);
  private readonly Paq8lApm _a4 = new(0x10000), _a5 = new(0x10000), _a6 = new(0x10000);
  private int _blockType, _blockLeft, _imageColumn;

  /// <summary>Probability (12 bits) that the next bit is 1.</summary>
  public int P { get; private set; } = 2048;

  public Paq8lPredictor(int level) {
    var h = this._h = new Paq8lHistory(level);
    var mem = h.Mem;
    this._orders = new Paq8lNibbleMap(h, mem * 32, 9);
    this._run1 = new Paq8lRunMap(h, mem);
    this._run2 = new Paq8lRunMap(h, mem);
    this._run3 = new Paq8lRunMap(h, mem);
    this._mixer = new Paq8lMixer(800, 3088, 7, 128);
    this._jpeg = new Paq8lJpegModel(h);
    this._match = new Paq8lMatchModel(h);
    this._image = new Paq8lImageModel(h);
    if (level < 4) return;
    this._sparse = new Paq8lSparseModel(h);
    this._distance = new Paq8lDistanceModel(h);
    this._fax = new Paq8lFaxModel(h);
    this._record = new Paq8lRecordModel(h);
    this._word = new Paq8lWordModel(h);
    this._indirect = new Paq8lIndirectModel(h);
    this._dmc = new Paq8lDmcModel(h);
    this._x86 = new Paq8lX86Model(h);
  }

  /// <summary>Takes in the coded bit and predicts the next one.</summary>
  public void Update(int bit) {
    var h = this._h;
    h.Push(bit);
    var q = this.Model();
    var c0 = h.C0;
    var b1 = (uint)h.Back(1);
    var order1 = (int)(c0 + 256 * b1);
    var order2 = (int)((uint)c0 ^ Paq8lTables.Hash(b1, (uint)h.Back(2)) & 0xFFFF);
    var order3 = (int)((uint)c0 ^ Paq8lTables.Hash(b1, (uint)h.Back(2), (uint)h.Back(3)) & 0xFFFF);
    var p = this._a.Refine(q, c0, bit);
    var p1 = this._a1.Refine(q, order1, bit);
    var p2 = this._a2.Refine(q, order2, bit);
    var p3 = this._a3.Refine(q, order3, bit);
    q = (q + p1 + p2 + p3 + 2) >> 2;
    p1 = this._a4.Refine(p, order1, bit);
    p2 = this._a5.Refine(p, order2, bit);
    p3 = this._a6.Refine(p, order3, bit);
    p = (p + p1 + p2 + p3 + 2) >> 2;
    this.P = (p + q + 1) >> 1;
  }

  private int Model() {
    var h = this._h;
    var m = this._mixer;
    var bit = h.Bit;
    var bpos = h.BitPos;

    if (bpos == 0) {
      --this._blockLeft;
      if (this._blockLeft == -1) this._blockType = h.Back(1);
      if (this._blockLeft == -5) {
        this._blockLeft = h.Back(4) << 24 | h.Back(3) << 16 | h.Back(2) << 8 | h.Back(1);
        if (this._blockType == 2) this._blockLeft += 8;
      }
    }

    m.Train(bit);
    m.Add(256);

    var isJpeg = this._jpeg.Mix(m);
    var matchClass = (int)Paq8lTables.Log[this._match.Mix(m) & 0xFFFF];
    var imageWidth = this._image.Mix(m);

    if (isJpeg != 0) {
      m.Select(1, 8);
      m.Select(h.C0, 256);
      m.Select(h.Back(1), 256);
      return m.Predict(bit);
    }
    if (imageWidth > 0) {
      if (++this._imageColumn >= 24) this._imageColumn = 0;
      m.Select(2, 8);
      m.Select(this._imageColumn, 24);
      m.Select((h.Back(imageWidth) + h.Back(3)) >> 4, 32);
      m.Select(h.C0, 256);
      return m.Predict(bit);
    }

    if (bpos == 0) {
      var c = h.C4 & 255;
      for (var i = 15; i > 0; --i) this._orderHash[i] = unchecked(this._orderHash[i - 1] * 257 + c + 1);
      for (var i = 0; i < 7; ++i) this._orders.Set(this._orderHash[i]);
      this._run1.Set(this._orderHash[7]);
      this._orders.Set(this._orderHash[8]);
      this._run2.Set(this._orderHash[10]);
      this._run3.Set(this._orderHash[12]);
      this._orders.Set(this._orderHash[14]);
    }
    var hits = this._orders.Mix(m);
    this._run1.Mix(m);
    this._run2.Mix(m);
    this._run3.Mix(m);

    if (h.Level >= 4) {
      this._sparse!.Mix(m, matchClass, hits);
      this._distance!.Mix(m);
      this._fax!.Mix(m);
      this._record!.Mix(m);
      this._word!.Mix(m);
      this._indirect!.Mix(m);
      this._dmc!.Mix(m);
      if (this._blockType == 2) this._x86!.Mix(m);
    }

    var order = Math.Max(0, hits - 2);
    int c1 = h.Back(1), c2 = h.Back(2), c3 = h.Back(3);
    m.Select(c1 + 8, 264);
    m.Select(h.C0, 256);
    m.Select(order + 8 * (int)(h.C4 >> 5 & 7) + 64 * (c1 == c2 ? 1 : 0) + 128 * (this._blockType == 2 ? 1 : 0), 256);
    m.Select(c2, 256);
    m.Select(c3, 256);
    m.Select(matchClass, 256);
    int ctx;
    if (bpos != 0) {
      ctx = h.C0 << (8 - bpos);
      if (bpos == 1) ctx += c3 / 2;
      ctx = Math.Min(bpos, 5) * 256 + c1 / 32 + 8 * (c2 / 32) + (ctx & 192);
    } else
      ctx = c3 / 128 + (int)(h.C4 >> 31) * 2 + 4 * (c2 / 64) + (c1 & 240);
    m.Select(ctx, 1536);
    return m.Predict(bit);
  }
}

/// <summary>The binary arithmetic coder (§3), driven by a <see cref="Paq8lPredictor"/>.</summary>
internal sealed class Paq8lEncoder(Stream output, int level) {
  private readonly Paq8lPredictor? _model = level > 0 ? new Paq8lPredictor(level) : null;
  private uint _x1, _x2 = 0xFFFFFFFF;

  public void Write(int value) {
    if (this._model == null) {
      output.WriteByte((byte)value);
      return;
    }
    for (var i = 7; i >= 0; --i) this.Code((value >> i) & 1);
  }

  private void Code(int bit) {
    var p = this._model!.P;
    p += p < 2048 ? 1 : 0;
    var range = this._x2 - this._x1;
    var mid = this._x1 + (range >> 12) * (uint)p + ((range & 0xFFF) * (uint)p >> 12);
    if (bit != 0) this._x2 = mid; else this._x1 = mid + 1;
    this._model.Update(bit);
    while (((this._x1 ^ this._x2) & 0xFF000000) == 0) {
      output.WriteByte((byte)(this._x2 >> 24));
      this._x1 <<= 8;
      this._x2 = (this._x2 << 8) + 255;
    }
  }

  public void Flush() {
    if (this._model != null) output.WriteByte((byte)(this._x1 >> 24));
  }
}

/// <summary>Decodes what <see cref="Paq8lEncoder"/> codes.</summary>
internal sealed class Paq8lDecoder {
  private readonly Paq8lPredictor? _model;
  private readonly Stream _input;
  private uint _x1, _x2 = 0xFFFFFFFF, _x;

  public Paq8lDecoder(Stream input, int level) {
    this._input = input;
    if (level <= 0) return;
    this._model = new Paq8lPredictor(level);
    for (var i = 0; i < 4; ++i) this._x = this._x << 8 | this.Next();
  }

  /// <summary>
  /// Bytes read past the end of the input. A complete stream leaves exactly 3 (the decoder
  /// reads 4 ahead; the encoder's last renormalisation writes 1), so more means it was cut.
  /// </summary>
  public int PastEnd { get; private set; }

  private uint Next() {
    var b = this._input.ReadByte();
    if (b < 0) ++this.PastEnd;
    return (uint)(b & 255); // past the end: -1 & 255 = 255, as the format reads it
  }

  /// <summary>Next byte; at level 0 −1 past the end.</summary>
  public int Read() {
    if (this._model == null) {
      var b = this._input.ReadByte();
      if (b < 0) ++this.PastEnd;
      return b;
    }
    var c = 0;
    for (var i = 0; i < 8; ++i) c += c + this.Code();
    return c;
  }

  private int Code() {
    var p = this._model!.P;
    p += p < 2048 ? 1 : 0;
    var range = this._x2 - this._x1;
    var mid = this._x1 + (range >> 12) * (uint)p + ((range & 0xFFF) * (uint)p >> 12);
    var bit = this._x <= mid ? 1 : 0;
    if (bit != 0) this._x2 = mid; else this._x1 = mid + 1;
    this._model.Update(bit);
    while (((this._x1 ^ this._x2) & 0xFF000000) == 0) {
      this._x1 <<= 8;
      this._x2 = (this._x2 << 8) + 255;
      this._x = this._x << 8 | this.Next();
    }
    return bit;
  }
}
