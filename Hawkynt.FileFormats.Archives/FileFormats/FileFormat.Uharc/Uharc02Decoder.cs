namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.2's main coder (methods 8..43, modes <c>-m1</c>..<c>-m3</c>), docs/UHARC-ON-DISK.md,
/// "UHARC 0.2": LZ77 with literals coded in 256 order-1 frequency contexts, driven by a token
/// model and a bit-wise range coder.
/// </summary>
internal sealed class Uharc02Decoder {

  // Token model: one cumulative row per state, symbols control / run / copy / literal.
  private static readonly ushort[] TokenSeed = [0, 1, 2, 0xC00, 0x2000];

  // Copy lengths: cumulative frequencies of 22 classes, base length and extra uniform range per class.
  private static readonly ushort[] LengthSeed = [
    0x0000, 0x0010, 0x0018, 0x0028, 0x0038, 0x0058, 0x007C, 0x00CC, 0x0130, 0x018C, 0x0244, 0x02EF,
    0x0401, 0x04C9, 0x05CD, 0x071F, 0x08E1, 0x0B39, 0x0E59, 0x1241, 0x17B9, 0x2119, 0x2710,
  ];

  private static readonly byte[] LengthBase = [
    0xFF, 0xBF, 0x7F, 0x5F, 0x3F, 0x2F, 0x1F, 0x17, 0x13, 0x0F, 0x0D, 0x0C, 0x0B, 0x0A, 0x09, 0x08,
    0x07, 0x06, 0x05, 0x04, 0x03, 0x02,
  ];

  private static readonly byte[] LengthExtra = [
    0x00, 0x40, 0x40, 0x20, 0x20, 0x10, 0x10, 0x08, 0x04, 0x04, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
  ];

  // Distance classes 0..39 (first distance, width), then 40 = distance 1, 41 = an older recent
  // distance, 42 = one of the three newest, 43 = the newest.
  private static readonly uint[] DistanceBase = [
    0x1E0000, 0x1C0000, 0x1A0000, 0x180000, 0x160000, 0x140000, 0x120000, 0x100000, 0x0E0000, 0x0C0000,
    0x0A0000, 0x080000, 0x060000, 0x040000, 0x030000, 0x020000, 0x018000, 0x010000, 0x00C000, 0x008000,
    0x006000, 0x004000, 0x003000, 0x002000, 0x001800, 0x001000, 0x000C00, 0x000800, 0x000600, 0x000400,
    0x000300, 0x000200, 0x000100, 0x000080, 0x000040, 0x000020, 0x000010, 0x000008, 0x000004, 0x000002,
    0x000001, 0x000003, 0x000001, 0x000000,
  ];

  private static readonly uint[] DistanceSpan = [
    0x20000, 0x20000, 0x20000, 0x20000, 0x20000, 0x20000, 0x20000, 0x20000, 0x20000, 0x20000,
    0x20000, 0x20000, 0x20000, 0x20000, 0x10000, 0x10000, 0x08000, 0x08000, 0x04000, 0x04000,
    0x02000, 0x02000, 0x01000, 0x01000, 0x00800, 0x00800, 0x00400, 0x00400, 0x00200, 0x00200,
    0x00100, 0x00100, 0x00100, 0x00080, 0x00040, 0x00020, 0x00010, 0x00008, 0x00004, 0x00002,
  ];

  // History lengths at which distance classes 36, 35, ... 0 open up.
  private static readonly uint[] OpenAt = [
    0xFFFFFFFF, 0x1E0000, 0x1C0000, 0x1A0000, 0x180000, 0x160000, 0x140000, 0x120000, 0x100000, 0x0E0000,
    0x0C0000, 0x0A0000, 0x080000, 0x060000, 0x040000, 0x030000, 0x020000, 0x018000, 0x010000, 0x00C000,
    0x008000, 0x006000, 0x004000, 0x003000, 0x002000, 0x001800, 0x001000, 0x000C00, 0x000800, 0x000600,
    0x000400, 0x000300, 0x000200, 0x000100, 0x000080, 0x000040, 0x000020,
  ];

  private static readonly ushort[] DistanceSeed = [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0x0200, 0x0400, 0x0600, 0x0800, 0x0A00, 0x0C00, 0x0E00, 0x1000,
  ];

  private static readonly ushort[] RecentSeed = [0x1000, 0x0000, 0x1194, 0x238C];

  private static readonly ushort[] OlderSeed = [
    0x0000, 0x0190, 0x0352, 0x0546, 0x076C, 0x09C4, 0x0C4E, 0x0F0A, 0x11F8, 0x1518, 0x186A, 0x1BEE,
    0x1FA4, 0x238C,
  ];

  private static readonly ushort[] RunSeed = [0x0000, 0x0780, 0x0C80, 0x0F00];

  // Literal context mapping for the multimedia modes.
  private static readonly byte[] PhaseOfClass = [0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0];

  private static readonly byte[] DeltaClass = [
    0, 1, 1, 2, 2, 2, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
    6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
    7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 8, 8, 8, 8, 8, 8, 8, 8, 8,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,
    9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
    10, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 12, 12, 12, 12, 12, 12, 12, 12, 13, 13, 13, 13, 13, 14, 14, 14, 15, 15,
  ];

  private static readonly byte[] TripleDeltaClass = [
    0, 1, 2, 3, 4, 5, 6, 7, 7, 8, 8, 9, 9, 10, 10, 10, 11, 11, 11, 11, 11, 12, 12, 12, 12, 12, 12, 12, 12, 13, 13, 13,
    13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 15,
    15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
    15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
    16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
    17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17,
    17, 17, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 19, 19, 19, 19, 19, 19, 19, 19, 19,
    19, 19, 19, 19, 20, 20, 20, 20, 20, 20, 20, 20, 21, 21, 21, 21, 21, 22, 22, 22, 23, 23, 24, 24, 25, 25, 26, 27, 28, 29, 30, 31,
  ];

  // Halving rounds when the literal step changes from order [old * 4 + new].
  private static readonly byte[] StepChangeHalvings = [0, 0, 1, 2, 2, 0, 0, 1, 4, 2, 0, 0, 6, 4, 2, 0];

  private static readonly ushort[] LiteralStep = [2, 8, 32, 128];

  private static readonly byte[] RecentCount = [0, 5, 8, 16];

  private readonly byte[] _source;
  private readonly int _end;
  private int _position;
  private int _bitMask;
  private byte _currentByte;

  private uint _code;
  private uint _range;

  private readonly ushort[] _tree = new ushort[256 * 256];
  private readonly ushort[] _frequency = new ushort[256 * 256];
  private readonly ushort[][] _token = new ushort[8][];
  private readonly ushort[] _lengthNew = (ushort[])LengthSeed.Clone();
  private readonly ushort[] _lengthRecent = (ushort[])LengthSeed.Clone();
  private readonly ushort[] _distance = (ushort[])DistanceSeed.Clone();
  private readonly ushort[] _recent3 = (ushort[])RecentSeed.Clone();
  private readonly ushort[] _older = (ushort[])OlderSeed.Clone();
  private readonly ushort[] _runLength = (ushort[])RunSeed.Clone();
  private readonly uint[] _recent = new uint[16];

  private readonly byte[] _window;
  private readonly uint _windowSize;
  private readonly uint _windowMask;
  private readonly uint _historyLimit;
  private uint _windowPosition;
  private uint _history = 31;
  private int _opened = OpenAt.Length - 1;

  private readonly int _recentCount;
  private readonly int _olderCount;

  private int _state = 7;
  private int _context = 0xFF;
  private int _mode;
  private int _phaseMask;
  private int _order = 3;
  private bool _flip = true;
  private bool _rawLiterals;
  private bool _upperLiterals;
  private uint _outputHistory = 0xFFFFFFFF;
  private uint _counter;

  private readonly byte[] _output;
  private int _written;

  private Uharc02Decoder(byte[] source, int offset, int length, int method, int unpackedSize) {
    this._source = source;
    this._position = offset;
    this._end = offset + length;
    var group = method < 20 ? 1 : method < 32 ? 2 : 3;
    this._recentCount = RecentCount[group];
    this._olderCount = RecentCount[group] - 3;
    this._windowSize = 1u << ((method - 8) % 12 + 10);
    this._windowMask = this._windowSize - 1;
    this._historyLimit = this._windowSize - 255;
    this._window = new byte[this._windowSize + 256];
    for (var i = 0; i < 8; ++i)
      this._token[i] = (ushort[])TokenSeed.Clone();
    Array.Fill(this._recent, uint.MaxValue);
    this.ResetLiterals();
    this._output = new byte[unpackedSize];
  }

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes.</summary>
  public static byte[] Decode(byte[] source, int offset, int length, int method, int unpackedSize) {
    if (method is < 8 or > 43)
      throw new ArgumentOutOfRangeException(nameof(method));

    var decoder = new Uharc02Decoder(source, offset, length, method, unpackedSize);
    if (unpackedSize > 0)
      decoder.Run();
    return decoder._output;
  }

  // ── Bit-wise range coder ───────────────────────────────────────────────

  private int Bit() {
    if (this._bitMask == 0) {
      if (this._position >= this._end) {
        // A few bits past the end are normal while the coder drains.
        if (this._position++ > this._end + 8)
          throw new InvalidDataException("UHARC: packed stream ends before the decoder finished.");
        this._currentByte = 0;
      } else
        this._currentByte = this._source[this._position++];
      this._bitMask = 0x80;
    }

    var bit = (this._currentByte & this._bitMask) != 0 ? 1 : 0;
    this._bitMask >>= 1;
    return bit;
  }

  private uint Target(uint total) {
    if (total == 0 || this._range == 0)
      throw new InvalidDataException("UHARC: empty frequency table.");
    return (uint)((((ulong)this._code + 1) * total - 1) / this._range);
  }

  private void Narrow(uint low, uint high, uint total) {
    var from = (uint)((ulong)low * this._range / total);
    var to = (uint)((ulong)high * this._range / total);
    this._code -= from;
    var span = to - from;
    while (span <= 0x8000) {
      span <<= 1;
      this._code = (this._code << 1) + (uint)this.Bit();
    }

    this._range = span;
  }

  private uint Uniform(uint total) {
    var value = this.Target(total);
    if (value >= total)
      throw new InvalidDataException("UHARC: range coder left its interval.");
    this.Narrow(value, value + 1, total);
    return value;
  }

  /// <summary>The highest symbol whose cumulative start the target reaches, in <c>[0, last)</c>.</summary>
  private int Cumulative(ushort[] model, int last) {
    var total = model[last];
    var target = this.Target(total);
    var symbol = last;
    do
      --symbol;
    while (symbol > 0 && target < model[symbol]);
    this.Narrow(model[symbol], model[symbol + 1], total);
    return symbol;
  }

  // ── Literal contexts ───────────────────────────────────────────────────

  private void ResetLiterals() {
    for (var context = 0; context < 256; ++context) {
      var b = context * 256;
      this._frequency.AsSpan(b, 256).Fill(1);
      this.BuildTree(context, 256);
    }
  }

  /// <summary>Rebuilds the context's cumulative tree (a Fenwick tree whose slot 0 holds the total).</summary>
  private void BuildTree(int context, uint total) {
    var b = context * 256;
    var tree = this._tree.AsSpan(b, 256);
    tree[0] = (ushort)total;
    for (var i = 1; i < 256; ++i)
      tree[i] = this._frequency[b + i - 1];
    for (var i = 1; i < 256; ++i) {
      var j = i + (i & -i);
      if (j < 256)
        tree[j] += tree[i];
    }
  }

  private void HalveFrequencies(int context) {
    var row = this._frequency.AsSpan(context * 256, 256);
    for (var i = 0; i < row.Length; ++i)
      row[i] = (ushort)((row[i] + 1) >> 1);
  }

  private void Rescale(int context, int rounds) {
    for (var i = 0; i < rounds; ++i)
      this.HalveFrequencies(context);
    uint total = 0;
    foreach (var f in this._frequency.AsSpan(context * 256, 256))
      total += f;
    this.BuildTree(context, total & 0xFFFF);
  }

  /// <summary>Decodes a byte from the context tree; <paramref name="upper"/> restricts it to 0x80..0xFF.</summary>
  private int DecodeFromTree(int context, bool upper) {
    var tree = this._tree.AsSpan(context * 256, 256);
    uint total = upper ? (ushort)(tree[0] - tree[128]) : tree[0];
    var target = this.Target(total);
    uint above = total;
    var index = 0;
    var symbol = 0;
    var step = 128;
    if (upper) {
      index = 128;
      symbol = 0x80;
      step = 64;
    }

    // Symbols are laid out from the top: a node's range sits above everything it skips.
    for (; step > 0; step >>= 1) {
      var candidate = (ushort)(above - tree[index + step]);
      if (target < candidate) {
        index += step;
        symbol |= step;
        above = candidate;
      }
    }

    uint low;
    if (symbol == 0xFF)
      low = 0;
    else if ((symbol & 1) == 0)
      low = (ushort)(above - tree[index + 1]);
    else {
      var next = (symbol + 1) & 0xFF;
      uint value = tree[0];
      var p = 0;
      for (var mask = 0x80; mask > 0; mask >>= 1)
        if ((next & mask) != 0) {
          p += mask;
          value = (ushort)(value - tree[p]);
        }

      low = value;
    }

    this.Narrow(low, above, total);
    return symbol;
  }

  private void CountLiteral(byte symbol) {
    var step = LiteralStep[this._order & 3];
    var b = this._context * 256;
    this._frequency[b + symbol] += step;
    if (symbol != 0xFF) {
      var i = (symbol + 1) & 0xFF;
      for (var bit = 1; i != 0 && bit < 256; bit <<= 1)
        if ((i & bit) != 0) {
          this._tree[b + i] += step;
          i = (i + bit) & 0xFF;
        }
    }

    this._tree[b] += step;
    if (this._tree[b] >= 0x4000)
      this.Rescale(this._context, 1);

    this._context = this._mode switch {
      0 or 5 => symbol,
      1 => (PhaseOfClass[this._context & 15] << 4) + DeltaClass[symbol],
      3 => TripleDeltaClass[symbol] + (int)((this._counter % 3) << 5),
      _ => ((int)(this._counter & (uint)this._phaseMask) << 4) + DeltaClass[symbol],
    };
    ++this._counter;
  }

  // ── Output ─────────────────────────────────────────────────────────────

  private byte Reference => (byte)(this._outputHistory >> (8 * (this._phaseMask & 3)));

  private void Put(byte value) {
    if (this._written < this._output.Length)
      this._output[this._written] = value;
    ++this._written;
  }

  /// <summary>Emits a coded literal: complement, and in a multimedia mode a signed difference from the reference.</summary>
  private byte EmitLiteral(int symbol) {
    var value = (byte)~symbol;
    if (this._mode != 0) {
      var difference = this._flip ? value : (byte)-value;
      if (value >= 0x80)
        this._flip = !this._flip;
      value = (byte)(difference + this.Reference);
    }

    this._outputHistory = (this._outputHistory << 8) + value;
    this.Put(value);
    return value;
  }

  /// <summary>Emits a copied byte and returns the symbol the literal model counts for it.</summary>
  private byte EmitCopied(byte value) {
    var difference = value;
    if (this._mode != 0) {
      difference = (byte)(value - this.Reference);
      if (!this._flip)
        difference = (byte)-difference;
      if (difference >= 0x80)
        this._flip = !this._flip;
    }

    this._outputHistory = (this._outputHistory << 8) + value;
    this.Put(value);
    return (byte)~difference;
  }

  private void StoreInWindow(byte value) {
    this._window[this._windowPosition] = value;
    if (this._history < this._historyLimit)
      ++this._history;
    this._windowPosition = (this._windowPosition + 1) & this._windowMask;
  }

  // ── Steps ──────────────────────────────────────────────────────────────

  private void Run() {
    for (var i = 0; i < 16; ++i)
      this._code = (this._code << 1) | (uint)this.Bit();
    this._range = 0x10000;

    while (this._written < this._output.Length) {
      var row = this._token[this._state];
      var token = this.Cumulative(row, 4);
      if (token >= 2) {
        if (token == 2)
          row[3] += 64;
        row[4] += 64;
        if (row[4] >= 0x4000) {
          var copyWidth = (row[3] - row[2] + 1) >> 1;
          var literalWidth = (row[4] - row[3] + 1) >> 1;
          row[3] = (ushort)(row[2] + copyWidth);
          row[4] = (ushort)(row[3] + literalWidth);
        }

        this._state = (token + 2 * this._state - 2) & 7;
      }

      switch (token) {
        case 0:
          this.Control();
          break;
        case 1:
          this.ByteRun();
          break;
        case 2:
          this.Copy();
          break;
        default:
          this.Literal();
          break;
      }
    }
  }

  private void Control() {
    var command = (int)this.Uniform(12);
    switch (command) {
      case <= 5:
        this._mode = command;
        this._phaseMask = (command - 1) & 3;
        if (this._written != 0)
          this.ResetLiterals();
        break;
      case <= 9: {
        var previous = this._order;
        this._order = command - 6;
        var rounds = StepChangeHalvings[4 * previous + this._order];
        if (this._written != 0 && rounds != 0)
          for (var context = 0; context < 256; ++context)
            this.Rescale(context, rounds);
        break;
      }
      case 10:
        this._rawLiterals = !this._rawLiterals;
        break;
      default:
        this._upperLiterals = !this._upperLiterals;
        break;
    }
  }

  private void Literal() {
    int symbol;
    if (this._rawLiterals)
      symbol = (int)this.Uniform(256);
    else
      symbol = this.DecodeFromTree(this._context, this._upperLiterals);

    this.CountLiteral((byte)symbol);
    this.StoreInWindow(this.EmitLiteral(symbol));
  }

  private void Copy() {
    while (this._history >= OpenAt[this._opened]) {
      var cls = this._opened--;
      var lift = (this._distance[cls + 1] + 1) >> 1;
      for (var a = cls; a < 45; ++a)
        this._distance[a] += (ushort)lift;
      RescaleCumulative(this._distance, 44, 0);
    }

    var cls2 = this.Cumulative(this._distance, 44);
    for (var a = cls2 + 1; a < 44; ++a)
      this._distance[a] += 0x20;
    this._distance[44] += 0x20;
    if (this._distance[44] >= 0x4000)
      RescaleCumulative(this._distance, 44, 0);

    uint distance;
    bool repeat;
    var shiftCount = this._recentCount - 1;
    var position = DistanceBase[cls2];
    switch (cls2) {
      case 40:
        repeat = false;
        distance = position;
        break;
      case 41: {
        var slot = this._olderCount - 1 - this.DecodeOlder();
        position += (uint)slot;
        repeat = true;
        break;
      }
      case 42:
        position += (uint)(1 - this.DecodeRecent3());
        repeat = true;
        break;
      case 43:
        repeat = true;
        break;
      default:
        position = this.RefineDistance(cls2, position);
        repeat = false;
        break;
    }

    if (repeat) {
      var index = (int)(position & 0xFF);
      if (index >= 16)
        throw new InvalidDataException("UHARC: recent-distance index out of range.");
      distance = this._recent[index];
      if (index != 0)
        this.CountRecent(index);
      shiftCount = index;
    } else
      distance = position;

    if (shiftCount != 0 && distance > 1) {
      for (var k = shiftCount; k > 0; --k)
        this._recent[k] = this._recent[k - 1];
      this._recent[0] = distance;
    }

    var lengths = repeat ? this._lengthRecent : this._lengthNew;
    var last = repeat ? 22 : 21;
    var lengthClass = this.Cumulative(lengths, last);
    var extra = LengthExtra[lengthClass] != 0 ? this.Uniform(LengthExtra[lengthClass]) : 0;
    for (var a = lengthClass + 1; a < last; ++a)
      lengths[a] += 0x40;
    lengths[last] += 0x40;
    if (lengths[last] >= 0x4000)
      RescaleCumulative(lengths, last, 0);

    var length = LengthBase[lengthClass] + (int)extra;
    if (length < 2)
      throw new InvalidDataException("UHARC: copy shorter than two bytes.");

    var source = (this._windowPosition - distance) & this._windowMask;
    for (var i = 0; i < length; ++i) {
      var b = this._window[source];
      source = (source + 1) & this._windowMask;
      this.StoreInWindow(b);
      this.CountLiteral(this.EmitCopied(b));
    }
  }

  private uint RefineDistance(int cls, uint position) {
    var span = DistanceSpan[cls];
    if (position + span - 1 > this._history)
      span = this._history - position + 1;
    if (span < 2)
      return position;

    if (span > 0x1000) {
      var blocks = ((span - 1) >> 12) + 1;
      var block = this.Uniform(blocks);
      position += block << 12;
      span = block + 1 == blocks ? span - (block << 12) : 0x1000;
    }

    if (span >= 2)
      position += this.Uniform(span);
    return position;
  }

  /// <summary>Slot among the older recent distances; the model has <see cref="_olderCount"/> symbols.</summary>
  private int DecodeOlder() {
    var model = this._older;
    var count = this._olderCount;
    var total = model[count];
    var target = this.Target(total);
    var symbol = count;
    do
      --symbol;
    while (symbol > 0 && target < model[symbol]);
    this.Narrow(model[symbol], model[symbol + 1], total);
    return symbol;
  }

  /// <summary>One of the three newest distances: model entries 1..3 over a fixed base 0.</summary>
  private int DecodeRecent3() {
    var model = this._recent3;
    var total = model[3];
    var target = this.Target(total);
    var symbol = 2;
    do
      --symbol;
    while (symbol > 0 && target < model[symbol + 1]);
    this.Narrow(model[symbol + 1], model[symbol + 2], total);
    return symbol;
  }

  private void CountRecent(int index) {
    if (index < 3) {
      var m = this._recent3;
      if (index == 2)
        m[2] += 0x20;
      m[3] += 0x20;
      if (m[3] >= 0x4000) {
        var a = Math.Max((m[2] - m[1] + 1) >> 1, 0x400);
        var b = Math.Max((m[3] - m[2] + 1) >> 1, 0x400);
        m[2] = (ushort)(m[1] + a);
        m[3] = (ushort)(m[2] + b);
      }

      return;
    }

    var model = this._older;
    var count = this._olderCount;
    for (var a = this._recentCount - index; a < count; ++a)
      model[a] += 0x20;
    model[count] += 0x20;
    if (model[count] >= 0x4000)
      RescaleCumulative(model, count, 0x100);
  }

  /// <summary>Halves every width of a cumulative model (rounding up, at least <paramref name="floor"/>).</summary>
  private static void RescaleCumulative(ushort[] model, int last, int floor) {
    for (var d = last; d >= 1; --d)
      model[d] = (ushort)Math.Max((model[d] - model[d - 1] + 1) >> 1, floor);
    for (var a = 2; a <= last; ++a)
      model[a] += model[a - 1];
  }

  private void ByteRun() {
    var cls = this.Cumulative(this._runLength, 3);
    var m = this._runLength;
    if (cls == 0)
      m[1] += 64;
    if (cls <= 1)
      m[2] += 64;
    m[3] += 64;
    if (m[3] >= 0x4000)
      RescaleCumulative(m, 3, 0);

    long length = 0x100;
    if (cls == 2)
      length += (long)this.Uniform(256) << 16;
    if (cls >= 1)
      length += (long)this.Uniform(256) << 8;
    length += this.Uniform(256);
    var value = (byte)this.Uniform(256);

    for (var i = 0; i < 255; ++i)
      this.StoreInWindow(value);

    byte symbol = 0;
    for (var i = 0; i < 5; ++i) {
      symbol = this.EmitCopied(value);
      this.CountLiteral(symbol);
    }

    // The rest bypasses the output transform: the model counts the fifth byte's symbol again.
    for (var i = 5; i < length; ++i) {
      this.Put(value);
      this.CountLiteral(symbol);
    }
  }
}
