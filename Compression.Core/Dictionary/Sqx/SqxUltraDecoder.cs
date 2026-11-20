namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// SQX 2.0 method 7 ("ultra"): LZ77 whose literals, match flags, lengths and distances are coded
/// with a carryless range coder over adaptive Fenwick-tree frequency models. Converted from
/// Squeez-c (MIT) src/ultra.c. In a solid chain the window and the models carry over from file to
/// file; the match state and repeated distances start afresh with every file.
/// </summary>
public sealed class SqxUltraDecoder {
  private static readonly uint[] DistBase = [
    0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768,
    1024, 1536, 2048, 3072, 4096, 6144, 8192, 12288, 16384, 24576, 32768, 49152,
    65536, 98304, 131072, 196608, 262144, 393216, 524288, 786432, 1048576, 1572864,
    2097152, 3145728, 4194304, 6291456, 8388608, 12582912, 16777216, 25165824,
  ];

  private static readonly byte[] DistExtra = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8,
    9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16,
    17, 17, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22, 23, 23,
  ];

  private static readonly uint[] LenBase = [2, 3, 4, 6, 10, 18, 34, 66, 130];
  private static readonly byte[] LenSymbols = [0, 0, 2, 4, 8, 16, 32, 64, 128];
  private static readonly byte[] LenIncrement = [0, 0, 1, 1, 2, 2, 2, 4, 4];

  private const uint Top = 1u << 24, Bottom = 1u << 16;

  private readonly BitModel[] _isMatch = new BitModel[64];
  private readonly BitModel[] _isRep = new BitModel[16];
  private readonly ByteModel[] _literal0 = new ByteModel[256];
  private readonly ByteModel[] _literalOther = new ByteModel[256];
  private readonly Fenwick[] _distSlot = new Fenwick[4];
  private readonly Fenwick _distLow4 = new(16, 4, 1023);
  private readonly Fenwick[] _lenSlot = new Fenwick[4];
  private readonly Fenwick[] _repLenSlot = new Fenwick[4];
  private readonly Fenwick?[,] _lenSub = new Fenwick?[4, 9];
  private readonly Fenwick?[,] _repLenSub = new Fenwick?[4, 9];
  private readonly Fenwick[] _repIndex = new Fenwick[4];

  private byte[] _window = [];
  private uint _mask;
  private uint _windowPos;
  private bool _initialized;

  // range coder
  private uint _low, _range, _code;
  private SqxBitReader _bits = null!;

  // LZ state
  private uint _state, _prevState, _prevByte, _savedRepDist, _savedRepLen;
  private readonly uint[] _rep = new uint[4];

  /// <summary>Creates a decoder; its models and window are set up by the first file decoded.</summary>
  public SqxUltraDecoder() {
    for (var i = 0; i < 64; ++i) this._isMatch[i] = new(8, 1023);
    for (var i = 0; i < 16; ++i) this._isRep[i] = new(8, 1023);
    for (var i = 0; i < 256; ++i) {
      this._literal0[i] = new(1);
      this._literalOther[i] = new(2);
    }
    for (var i = 0; i < 4; ++i) {
      this._distSlot[i] = new(50, 4, 1023);
      this._lenSlot[i] = new(9, 8, 1023);
      this._repLenSlot[i] = new(9, 8, 1023);
      this._repIndex[i] = new(6, 4, 1023);
      for (var s = 0; s < 9; ++s)
        if (LenSymbols[s] > 0) {
          this._lenSub[i, s] = new(LenSymbols[s], LenIncrement[s], 255);
          this._repLenSub[i, s] = new(LenSymbols[s], LenIncrement[s], 255);
        }
    }
  }

  /// <summary>
  /// Decodes one file of <paramref name="originalSize"/> bytes. With <paramref name="solid"/> the
  /// window and models of the file before are kept.
  /// </summary>
  public byte[] Decode(byte[] packed, long originalSize, bool solid, int dictionarySize) {
    if (originalSize is < 0 or > int.MaxValue)
      throw new NotSupportedException("SQX: ultra files over 2 GiB are not supported.");
    var size = 32768u;
    var wanted = dictionarySize <= 0 ? 4u << 20 : (uint)dictionarySize;
    while (size < wanted && size < 0x40000000u)
      size <<= 1;
    if (this._window.Length != size) {
      this._window = new byte[size];
      this._mask = size - 1;
      this._windowPos = 0;
      this._initialized = false;
    }
    if (originalSize == 0)
      return [];
    if (!solid || !this._initialized) {
      this.ResetModels();
      this._windowPos = 0;
      Array.Clear(this._window);
    }

    this._state = this._prevState = this._prevByte = 0;
    Array.Clear(this._rep);
    this._savedRepDist = this._savedRepLen = 0;
    this._bits = new SqxBitReader(packed, 0, packed.Length);
    this._low = 0;
    this._range = 0xFFFFFFFFu;
    this._code = 0;
    for (var i = 0; i < 4; ++i)
      this._code = (this._code << 8) | this._bits.Read(8);

    var output = new byte[originalSize];
    var outLength = 0;
    long position = 0;
    while (outLength < originalSize) {
      // the range coder reads a few bytes ahead; much further means the stream is cut short
      if (this._bits.Position > (packed.Length + 16L) * 8)
        throw new InvalidDataException("SQX: ultra stream ends early.");
      var posState = (uint)(position & 3);
      var matchContext = (this._prevState * 4 + this._state) * 4 + posState;
      if (!this.DecodeBit(this._isMatch[matchContext])) {
        var model = this._state == 0 ? this._literal0[this._prevByte] : this._literalOther[this._prevByte];
        var high = this.DecodeSymbol(model.High);
        var symbol = (byte)((high << 4) | this.DecodeSymbol(model.Low[high]));
        this._window[this._windowPos] = symbol;
        this._windowPos = (this._windowPos + 1) & this._mask;
        output[outLength++] = symbol;
        this._prevByte = symbol;
        this._prevState = this._state;
        this._state = 0;
        ++position;
        continue;
      }

      uint distance, length, nextState;
      if (this.DecodeBit(this._isRep[this._prevState * 4 + this._state])) {
        var repIndex = this.DecodeSymbol(this._repIndex[this._state]);
        if (repIndex == 4) {
          distance = this._rep[0] + 1;
          length = 1;
          nextState = 3;
        } else if (repIndex == 5) {
          distance = this._savedRepDist + 1;
          length = this._savedRepLen;
          nextState = 2;
        } else {
          if (repIndex > 3) repIndex = 3;
          length = this.DecodeLength(rep: true);
          var repDist = this._rep[repIndex];
          this._savedRepLen = length;
          this._savedRepDist = repDist;
          for (var i = repIndex; i > 0; --i)
            this._rep[i] = this._rep[i - 1];
          this._rep[0] = repDist;
          distance = repDist + 1;
          nextState = 2;
        }
      } else {
        length = this.DecodeLength(rep: false);
        var distIndex = length switch { 2 => 0, 3 => 1, 4 => 2, _ => 3 };
        var slot = Math.Min(this.DecodeSymbol(this._distSlot[distIndex]), 49u);
        var extra = DistExtra[slot];
        uint dist;
        if (extra == 0)
          dist = DistBase[slot];
        else if (extra < 4)
          dist = DistBase[slot] + this.DecodeDirect(extra);
        else {
          var direct = this.DecodeDirect(extra - 4);
          dist = DistBase[slot] + (direct << 4) + this.DecodeSymbol(this._distLow4);
        }
        this._rep[3] = this._rep[2];
        this._rep[2] = this._rep[1];
        this._rep[1] = this._rep[0];
        this._rep[0] = dist;
        distance = dist + 1;
        nextState = 1;
      }

      if (length == 0)
        throw new InvalidDataException("SQX: ultra stream repeats an empty match.");
      for (var i = 0u; i < length && outLength < originalSize; ++i) {
        var b = this._window[(this._windowPos - distance) & this._mask];
        this._window[this._windowPos] = b;
        this._windowPos = (this._windowPos + 1) & this._mask;
        output[outLength++] = b;
      }
      this._prevByte = this._window[(this._windowPos - distance) & this._mask];
      this._prevState = this._state;
      this._state = nextState;
      position += length;
    }
    this._initialized = true;
    return output;
  }

  private void ResetModels() {
    foreach (var m in this._isMatch) m.Reset();
    foreach (var m in this._isRep) m.Reset();
    foreach (var m in this._literal0) m.Reset();
    foreach (var m in this._literalOther) m.Reset();
    foreach (var m in this._distSlot) m.Reset();
    this._distLow4.Reset();
    for (var i = 0; i < 4; ++i) {
      this._lenSlot[i].Reset();
      this._repLenSlot[i].Reset();
      this._repIndex[i].Reset();
      for (var s = 2; s < 9; ++s) {
        this._lenSub[i, s]!.Reset();
        this._repLenSub[i, s]!.Reset();
      }
    }
  }

  private uint DecodeLength(bool rep) {
    var slot = Math.Min(this.DecodeSymbol((rep ? this._repLenSlot : this._lenSlot)[this._state]), 8u);
    var sub = 0u;
    if (LenSymbols[slot] > 0)
      sub = this.DecodeSymbol((rep ? this._repLenSub : this._lenSub)[this._state, slot]!);
    var length = LenBase[slot] + sub;
    if (length == 257)
      length = 257 + this.DecodeDirect(14);
    return length;
  }

  private void Normalize() {
    // a range that collapsed to nothing only comes from data no encoder wrote
    if (this._range == 0)
      throw new InvalidDataException("SQX: the ultra stream is malformed.");
    while (true) {
      if ((this._low ^ (this._low + this._range)) >= Top) {
        if (this._range >= Bottom)
          return;
        this._range = (0u - this._low) & (Bottom - 1);
        if (this._range == 0)
          throw new InvalidDataException("SQX: the ultra stream is malformed.");
      }
      this._code = (this._code << 8) | this._bits.Read(8);
      this._range <<= 8;
      this._low <<= 8;
    }
  }

  private uint DecodeDirect(int count) {
    var value = 0u;
    for (var i = 0; i < count; ++i) {
      this._range >>= 1;
      var bit = 0u;
      if (this._code - this._low >= this._range) {
        bit = 1;
        this._low += this._range;
      }
      value = (value << 1) | bit;
      this.Normalize();
    }
    return value;
  }

  private bool DecodeBit(BitModel m) {
    var step = this._range / m.Total;
    this._range = step;
    var count = step == 0 ? m.Total : (this._code - this._low) / step;
    var bit = false;
    if (count < m.Zero) {
      this._range = step * m.Zero;
      m.Zero += m.Increment;
    } else if (count < m.Total) {
      bit = true;
      this._low += step * m.Zero;
      this._range = step * m.One;
      m.One += m.Increment;
    }
    this.Normalize();
    m.Total += m.Increment;
    if (m.Total > m.MaxFrequency) {
      m.Zero -= m.Zero >> 1;
      m.One -= m.One >> 1;
      m.Total = m.Zero + m.One;
    }
    return bit;
  }

  private uint DecodeSymbol(Fenwick m) {
    var step = this._range / m.Total;
    this._range = step;
    var count = step == 0 ? m.Total : (this._code - this._low) / step;
    if (count >= m.Total)
      return 0;
    var (symbol, cumulative) = m.Find(count);
    this._low += step * cumulative;
    this._range = step * m.Frequency[symbol];
    this.Normalize();
    m.Update(symbol);
    return symbol;
  }

  /// <summary>An adaptive binary model: two counts, halved past a limit.</summary>
  private sealed class BitModel(uint increment, uint maxFrequency) {
    public readonly uint Increment = increment, MaxFrequency = maxFrequency;
    public uint Zero = increment, One = increment, Total = increment * 2;

    public void Reset() {
      this.Zero = this.One = this.Increment;
      this.Total = this.Increment * 2;
    }
  }

  /// <summary>A byte as two nibbles: the high one, then the low one in the high one's model.</summary>
  private sealed class ByteModel {
    public readonly Fenwick High;
    public readonly Fenwick[] Low = new Fenwick[16];

    public ByteModel(uint increment) {
      this.High = new(16, increment, 511);
      for (var i = 0; i < 16; ++i)
        this.Low[i] = new(16, increment, 511);
    }

    public void Reset() {
      this.High.Reset();
      foreach (var l in this.Low) l.Reset();
    }
  }

  /// <summary>
  /// An adaptive frequency model over a Fenwick tree whose slot 0 holds symbol 0's count alone;
  /// every count is halved when the total passes the limit.
  /// </summary>
  private sealed class Fenwick {
    public readonly uint[] Frequency;
    private readonly uint[] _tree;
    private readonly uint _symbols, _increment, _maxFrequency, _powerOfTwo;
    public uint Total;

    public Fenwick(uint symbols, uint increment, uint maxFrequency) {
      this._symbols = symbols;
      this._increment = increment;
      this._maxFrequency = maxFrequency;
      this.Frequency = new uint[symbols];
      this._tree = new uint[symbols];
      var p = 1u;
      while (symbols > p)
        p <<= 1;
      this._powerOfTwo = p >> 1;
      this.Reset();
    }

    public void Reset() {
      this.Total = this._symbols * this._increment;
      Array.Fill(this.Frequency, this._increment);
      Array.Clear(this._tree);
      this._tree[0] = this._increment;
      for (var i = 1u; i < this._symbols; ++i)
        this.Add(i, this._increment);
    }

    private void Add(uint index, uint delta) {
      while (index < this._symbols) {
        this._tree[index] += delta;
        index += index & (0u - index);
      }
    }

    public (uint Symbol, uint Cumulative) Find(uint count) {
      if (count < this._tree[0])
        return (0, 0);
      var remaining = count - this._tree[0] + 1;
      var index = 0u;
      for (var step = this._powerOfTwo; step > 0; step >>= 1) {
        var candidate = index + step;
        if (candidate < this._symbols && remaining > this._tree[candidate]) {
          index = candidate;
          remaining -= this._tree[candidate];
        }
      }
      var symbol = Math.Min(index + 1, this._symbols - 1);
      var cumulative = this._tree[0];
      for (var k = symbol - 1; k > 0; k &= k - 1)
        cumulative += this._tree[k];
      return (symbol, cumulative);
    }

    public void Update(uint symbol) {
      this.Frequency[symbol] += this._increment;
      if (symbol == 0) this._tree[0] += this._increment;
      else this.Add(symbol, this._increment);
      this.Total += this._increment;
      if (this.Total <= this._maxFrequency)
        return;
      for (var i = 0u; i < this._symbols; ++i) {
        var d = this.Frequency[i] >> 1;
        if (i == 0) this._tree[0] -= d;
        else this.Add(i, 0u - d);
        this.Total -= d;
        this.Frequency[i] -= d;
      }
    }
  }
}
