namespace FileFormat.Dms;

/// <summary>
/// The DMS track decompressors (RLE, Quick, Medium, Deep, Heavy 1/2) and the state they
/// share from track to track.
/// </summary>
/// <remarks>
/// Converted from libxad's DMS client (<c>libxad/clients/DMS.c</c>, Dirk Stöcker, LGPL-2.1-or-later),
/// see <c>THIRD-PARTY-NOTICE.libxad.txt</c>. DMS carries dictionary and model state across
/// tracks unless a track resets it, so one decoder must see every track in file order.
/// </remarks>
internal sealed class DmsDecoder {
  private const int Nc = 510;
  private const int Npt = 30;
  private const int N1 = 510;
  private const int Offset = 253;
  private const int LookAhead = 60;
  private const int Threshold = 2;
  private const int NByte = 256 - Threshold + LookAhead;
  private const int T = NByte * 2 - 1;
  private const int R = T - 1;
  private const int MaxFreq = 0x8000;

  private static ReadOnlySpan<byte> DCode => [
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
    0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02,
    0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
    0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
    0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
    0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x09, 0x09, 0x09, 0x09, 0x09, 0x09, 0x09, 0x09,
    0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B,
    0x0C, 0x0C, 0x0C, 0x0C, 0x0D, 0x0D, 0x0D, 0x0D, 0x0E, 0x0E, 0x0E, 0x0E, 0x0F, 0x0F, 0x0F, 0x0F,
    0x10, 0x10, 0x10, 0x10, 0x11, 0x11, 0x11, 0x11, 0x12, 0x12, 0x12, 0x12, 0x13, 0x13, 0x13, 0x13,
    0x14, 0x14, 0x14, 0x14, 0x15, 0x15, 0x15, 0x15, 0x16, 0x16, 0x16, 0x16, 0x17, 0x17, 0x17, 0x17,
    0x18, 0x18, 0x19, 0x19, 0x1A, 0x1A, 0x1B, 0x1B, 0x1C, 0x1C, 0x1D, 0x1D, 0x1E, 0x1E, 0x1F, 0x1F,
    0x20, 0x20, 0x21, 0x21, 0x22, 0x22, 0x23, 0x23, 0x24, 0x24, 0x25, 0x25, 0x26, 0x26, 0x27, 0x27,
    0x28, 0x28, 0x29, 0x29, 0x2A, 0x2A, 0x2B, 0x2B, 0x2C, 0x2C, 0x2D, 0x2D, 0x2E, 0x2E, 0x2F, 0x2F,
    0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
  ];

  private static ReadOnlySpan<byte> DLen => [
    0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
    0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
    0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
    0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
    0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
    0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
    0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
    0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
    0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
    0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
    0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
    0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
    0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
    0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
    0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
    0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08,
  ];

  private readonly byte[] _text = new byte[32768];
  private ushort _rtvQuick, _rtvMedium, _rtvDeep, _rtvHeavy;
  private bool _didInit, _didInitDeep;

  // Deep: adaptive Huffman tree.
  private readonly ushort[] _freq = new ushort[T + 1];
  private readonly ushort[] _prnt = new ushort[T + NByte];
  private readonly ushort[] _son = new ushort[T];

  // Heavy: static Huffman tables, kept between tracks that do not resend them.
  private readonly ushort[] _cTable = new ushort[4096];
  private readonly ushort[] _ptTable = new ushort[256];
  private readonly ushort[] _left = new ushort[2 * Nc - 1];
  private readonly ushort[] _right = new ushort[2 * Nc - 1 + 9];
  private readonly byte[] _cLen = new byte[Nc];
  private readonly byte[] _ptLen = new byte[Npt];
  private ushort _lastLen;
  private ushort _np;

  // Bit reader.
  private byte[] _in = [];
  private int _inPos;
  private uint _bitBuf;
  private int _bitCount;

  public DmsDecoder() => this.InitData();

  /// <summary>Decodes one track's payload; <paramref name="packed"/> must already be decrypted.</summary>
  public byte[] DecodeTrack(byte[] packed, DmsTrackHeader t) {
    var data = t.Mode switch {
      DmsConstants.ModeNone => packed.Length >= t.UnpackedSize ? packed[..t.UnpackedSize] : throw Bad("stored track shorter than its size"),
      DmsConstants.ModeSimple => UnpackRle(packed, t.UnpackedSize),
      DmsConstants.ModeQuick => UnpackRle(this.UnpackQuick(packed, t.RuntimeSize), t.UnpackedSize),
      DmsConstants.ModeMedium => UnpackRle(this.UnpackMedium(packed, t.RuntimeSize), t.UnpackedSize),
      DmsConstants.ModeDeep => UnpackRle(this.UnpackDeep(packed, t.RuntimeSize), t.UnpackedSize),
      DmsConstants.ModeHeavy1 or DmsConstants.ModeHeavy2 => this.UnpackHeavyTrack(packed, t),
      _ => throw new NotSupportedException($"DMS compression mode {t.Mode} is unknown."),
    };
    if ((t.Flags & DmsConstants.FlagNoInit) == 0 && !this._didInit)
      this.InitData();
    return data;
  }

  private byte[] UnpackHeavyTrack(byte[] packed, DmsTrackHeader t) {
    var flags = t.Mode == DmsConstants.ModeHeavy1 ? t.Flags & ~DmsConstants.FlagHeavy2Own : t.Flags | DmsConstants.FlagHeavy2Own;
    var data = this.UnpackHeavy(packed, t.RuntimeSize, flags);
    return (t.Flags & DmsConstants.FlagHeavyRle) != 0 ? UnpackRle(data, t.UnpackedSize) : data;
  }

  private void InitData() {
    this._rtvQuick = 251;
    this._rtvMedium = 0x3FBE;
    this._rtvDeep = 0x3FC4;
    if (!this._didInitDeep) {
      for (var i = 0; i < NByte; ++i) {
        this._freq[i] = 1;
        this._son[i] = (ushort)(i + T);
        this._prnt[i + T] = (ushort)i;
      }
      for (int i = 0, j = NByte; j <= R; i += 2, ++j) {
        this._freq[j] = (ushort)(this._freq[i] + this._freq[i + 1]);
        this._son[j] = (ushort)i;
        this._prnt[i] = this._prnt[i + 1] = (ushort)j;
      }
      this._freq[T] = 0xFFFF;
      this._prnt[R] = 0;
    }
    this._didInit = this._didInitDeep = true;
    Array.Clear(this._text, 0, 0x3FC8);
  }

  // ── bit reader: MSB-first, refilled a byte at a time to keep at least 16 bits ──

  private void InitBits(byte[] input) {
    this._in = input;
    this._inPos = 0;
    this._bitBuf = 0;
    this._bitCount = 0;
    this.Drop(0);
  }

  private ushort Get(int n) => (ushort)(this._bitBuf >> (this._bitCount - n));

  private void Drop(int n) {
    this._bitCount -= n;
    this._bitBuf &= (1u << this._bitCount) - 1;
    while (this._bitCount < 16) {
      this._bitBuf = this._bitBuf << 8 | (this._inPos < this._in.Length ? this._in[this._inPos] : 0u);
      ++this._inPos;
      this._bitCount += 8;
    }
    if (this._inPos > this._in.Length + 8)
      throw Bad("compressed track data ended early");
  }

  // ── Simple: 0x90 escape run-length coding ──

  private static byte[] UnpackRle(byte[] input, int size) {
    var output = new byte[size];
    int o = 0, i = 0;
    while (o < size) {
      var a = At(input, i++);
      if (a != 0x90) { output[o++] = a; continue; }
      var b = At(input, i++);
      if (b == 0) { output[o++] = a; continue; }
      a = At(input, i++);
      int n = b;
      if (b == 0xFF) {
        n = At(input, i++) << 8;
        n += At(input, i++);
      }
      if (o + n > size) throw Bad("RLE run past the track end");
      if (n == 0) break;
      output.AsSpan(o, n).Fill(a);
      o += n;
    }
    if (i > input.Length + 1) throw Bad("RLE data ended early");
    return output;
  }

  private static byte At(byte[] input, int index) => index < input.Length ? input[index] : (byte)0;

  // ── Quick: LZ77, 256-byte window ──

  private byte[] UnpackQuick(byte[] input, int size) {
    var output = new byte[size];
    this._didInit = false;
    this.InitBits(input);
    var o = 0;
    while (o < size) {
      if (this.Get(1) != 0) {
        this.Drop(1);
        output[o++] = this._text[this._rtvQuick++ & 0xFF] = (byte)this.Get(8);
        this.Drop(8);
      } else {
        this.Drop(1);
        var j = this.Get(2) + 2;
        this.Drop(2);
        var i = (ushort)(this._rtvQuick - this.Get(8) - 1);
        this.Drop(8);
        if (o + j > size) throw Bad("Quick match past the track end");
        while (j-- > 0)
          output[o++] = this._text[this._rtvQuick++ & 0xFF] = this._text[i++ & 0xFF];
      }
    }
    this._rtvQuick = (ushort)((this._rtvQuick + 5) & 0xFF);
    return output;
  }

  // ── Medium: LZ77, 16 KiB window, coded positions ──

  private byte[] UnpackMedium(byte[] input, int size) {
    var output = new byte[size];
    this._didInit = false;
    this.InitBits(input);
    var o = 0;
    while (o < size) {
      if (this.Get(1) != 0) {
        this.Drop(1);
        output[o++] = this._text[this._rtvMedium++ & 0x3FFF] = (byte)this.Get(8);
        this.Drop(8);
      } else {
        this.Drop(1);
        int c = this.Get(8);
        this.Drop(8);
        var j = DCode[c] + 3;
        int u = DLen[c];
        c = ((c << u) | this.Get(u)) & 0xFF;
        this.Drop(u);
        u = DLen[c];
        c = (DCode[c] << 8) | (((c << u) | this.Get(u)) & 0xFF);
        this.Drop(u);
        var i = (ushort)(this._rtvMedium - c - 1);
        if (o + j > size) throw Bad("Medium match past the track end");
        while (j-- > 0)
          output[o++] = this._text[this._rtvMedium++ & 0x3FFF] = this._text[i++ & 0x3FFF];
      }
    }
    this._rtvMedium = (ushort)((this._rtvMedium + 66) & 0x3FFF);
    return output;
  }

  // ── Deep: LZ77 with an adaptive Huffman (LZHUF-style) literal/length model ──

  private void Reconstruct() {
    var j = 0;
    for (var i = 0; i < T; ++i)
      if (this._son[i] >= T) {
        this._freq[j] = (ushort)((this._freq[i] + 1) / 2);
        this._son[j] = this._son[i];
        ++j;
      }
    for (int i = 0, k2 = NByte; k2 < T; i += 2, ++k2) {
      var f = this._freq[k2] = (ushort)(this._freq[i] + this._freq[i + 1]);
      var k = k2 - 1;
      while (f < this._freq[k]) --k;
      ++k;
      for (var l = k2; l > k; --l) {
        this._freq[l] = this._freq[l - 1];
        this._son[l] = this._son[l - 1];
      }
      this._freq[k] = f;
      this._son[k] = (ushort)i;
    }
    for (var i = 0; i < T; ++i) {
      var k = this._son[i];
      if (k >= T) this._prnt[k] = (ushort)i;
      else this._prnt[k] = this._prnt[k + 1] = (ushort)i;
    }
  }

  private void Update(int c) {
    if (this._freq[R] == MaxFreq)
      this.Reconstruct();
    c = this._prnt[c + T];
    do {
      var k = ++this._freq[c];
      var l = c + 1;
      if (k > this._freq[l]) {
        while (k > this._freq[++l]) { }
        --l;
        this._freq[c] = this._freq[l];
        this._freq[l] = k;

        int i = this._son[c];
        this._prnt[i] = (ushort)l;
        if (i < T) this._prnt[i + 1] = (ushort)l;

        int j = this._son[l];
        this._son[l] = (ushort)i;
        this._prnt[j] = (ushort)c;
        if (j < T) this._prnt[j + 1] = (ushort)c;
        this._son[c] = (ushort)j;
        c = l;
      }
    } while ((c = this._prnt[c]) != 0);
  }

  private int DecodeChar() {
    int c = this._son[R];
    while (c < T) {
      c = this._son[c + this.Get(1)];
      this.Drop(1);
    }
    c -= T;
    this.Update(c);
    return c;
  }

  private int DecodePosition() {
    int i = this.Get(8);
    this.Drop(8);
    var c = DCode[i] << 8;
    int j = DLen[i];
    i = ((i << j) | this.Get(j)) & 0xFF;
    this.Drop(j);
    return c | i;
  }

  private byte[] UnpackDeep(byte[] input, int size) {
    var output = new byte[size];
    this._didInitDeep = this._didInit = false;
    this.InitBits(input);
    var o = 0;
    while (o < size) {
      var c = this.DecodeChar();
      if (c < 256) {
        output[o++] = this._text[this._rtvDeep++ & 0x3FFF] = (byte)c;
        continue;
      }
      var j = c - 255 + Threshold;
      var i = (ushort)(this._rtvDeep - this.DecodePosition() - 1);
      if (o + j > size) throw Bad("Deep match past the track end");
      while (j-- > 0)
        output[o++] = this._text[this._rtvDeep++ & 0x3FFF] = this._text[i++ & 0x3FFF];
    }
    this._rtvDeep = (ushort)((this._rtvDeep + 60) & 0x3FFF);
    return output;
  }

  // ── Heavy: LZ77 with static Huffman tables (LHA-style), 4 KiB or 8 KiB window ──

  private sealed class TableBuilder(ushort[] left, ushort[] right, byte[] bitLength, int symbolCount, ushort[] table, int tableBits) {
    private readonly int _n = symbolCount;
    private int _c = -1, _len = 1, _depth = 1, _avail = symbolCount, _codeword;
    private readonly int _tableSize = 1 << tableBits;
    private readonly int _maxDepth = tableBits + 1;
    private int _bit = (1 << tableBits) / 2;
    public int Error { get; private set; }

    public void Build() {
      this.Make();
      this.Make();
      if (this.Error == 0 && this._codeword != this._tableSize) this.Error = 5;
    }

    private ushort Make() {
      ushort i = 0;
      if (this.Error != 0) return 0;
      if (this._len == this._depth) {
        while (++this._c < this._n)
          if (bitLength[this._c] == this._len) {
            i = (ushort)this._codeword;
            this._codeword += this._bit;
            if (this._codeword > this._tableSize) { this.Error = 1; return 0; }
            while (i < this._codeword) table[i++] = (ushort)this._c;
            return (ushort)this._c;
          }
        this._c = -1;
        ++this._len;
        this._bit >>= 1;
      }
      ++this._depth;
      if (this._depth < this._maxDepth) {
        this.Make();
        this.Make();
      } else if (this._depth > 32) {
        this.Error = 2;
        return 0;
      } else {
        if ((i = (ushort)this._avail++) >= 2 * this._n - 1) { this.Error = 3; return 0; }
        left[i] = this.Make();
        right[i] = this.Make();
        if (this._codeword >= this._tableSize) { this.Error = 4; return 0; }
        if (this._depth == this._maxDepth) table[this._codeword++] = i;
      }
      --this._depth;
      return i;
    }
  }

  private void MakeTable(int nchar, byte[] bitLength, int tableBits, ushort[] table) {
    var builder = new TableBuilder(this._left, this._right, bitLength, nchar, table, tableBits);
    builder.Build();
    if (builder.Error != 0)
      throw Bad($"Heavy Huffman table is malformed (code {builder.Error})");
  }

  private int DecodeC() {
    int j = this._cTable[this.Get(12)];
    if (j < N1) {
      this.Drop(this._cLen[j]);
    } else {
      this.Drop(12);
      int i = this.Get(16);
      var m = 0x8000;
      do {
        j = (i & m) != 0 ? this._right[j] : this._left[j];
        m >>= 1;
      } while (j >= N1 && m != 0);
      if (j >= N1) throw Bad("Heavy literal code does not resolve");
      this.Drop(this._cLen[j] - 12);
    }
    return j;
  }

  private int DecodeP() {
    int j = this._ptTable[this.Get(8)];
    if (j < this._np) {
      this.Drop(this._ptLen[j]);
    } else {
      this.Drop(8);
      int i = this.Get(16);
      var m = 0x8000;
      do {
        j = (i & m) != 0 ? this._right[j] : this._left[j];
        m >>= 1;
      } while (j >= this._np && m != 0);
      if (j >= this._np) throw Bad("Heavy position code does not resolve");
      this.Drop(this._ptLen[j] - 8);
    }
    if (j != this._np - 1) {
      if (j > 0) {
        var bits = j - 1;
        j = this.Get(bits) | (1 << (j - 1));
        this.Drop(bits);
      }
      this._lastLen = (ushort)j;
    }
    return this._lastLen;
  }

  private void ReadTreeC() {
    int n = this.Get(9);
    this.Drop(9);
    if (n > 0) {
      if (n > Nc) throw Bad("Heavy literal table too long");
      for (var i = 0; i < n; ++i) {
        this._cLen[i] = (byte)this.Get(5);
        this.Drop(5);
      }
      Array.Clear(this._cLen, n, Nc - n);
      this.MakeTable(Nc, this._cLen, 12, this._cTable);
    } else {
      n = this.Get(9);
      this.Drop(9);
      Array.Clear(this._cLen);
      Array.Fill(this._cTable, (ushort)n);
    }
  }

  private void ReadTreeP() {
    int n = this.Get(5);
    this.Drop(5);
    if (n > 0) {
      if (n > this._np) throw Bad("Heavy position table too long");
      for (var i = 0; i < n; ++i) {
        this._ptLen[i] = (byte)this.Get(4);
        this.Drop(4);
      }
      Array.Clear(this._ptLen, n, this._np - n);
      this.MakeTable(this._np, this._ptLen, 8, this._ptTable);
    } else {
      n = this.Get(5);
      this.Drop(5);
      Array.Clear(this._ptLen, 0, this._np);
      Array.Fill(this._ptTable, (ushort)n);
    }
  }

  private byte[] UnpackHeavy(byte[] input, int size, int flags) {
    var output = new byte[size];
    this._didInit = false;
    int mask;
    if ((flags & DmsConstants.FlagHeavy2Own) != 0) {
      this._np = 15;
      mask = 0x1FFF;
    } else {
      this._np = 14;
      mask = 0x0FFF;
    }
    this.InitBits(input);
    if ((flags & DmsConstants.FlagHeavyTables) != 0) {
      this.ReadTreeC();
      this.ReadTreeP();
    }
    var o = 0;
    while (o < size) {
      var c = this.DecodeC();
      if (c < 256) {
        output[o++] = this._text[this._rtvHeavy++ & mask] = (byte)c;
        continue;
      }
      var j = c - Offset;
      var i = (ushort)(this._rtvHeavy - this.DecodeP() - 1);
      if (o + j > size) throw Bad("Heavy match past the track end");
      while (j-- > 0)
        output[o++] = this._text[this._rtvHeavy++ & mask] = this._text[i++ & mask];
    }
    return output;
  }

  private static InvalidDataException Bad(string message) => new($"DMS: {message}.");
}
