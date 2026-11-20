namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.3 ALZ decoder (stream methods 36..79, modes <c>-m1</c>..<c>-m3</c>), implemented from
/// docs/UHARC-ON-DISK.md section "UHARC 0.3 ALZ". The model is <see cref="Uharc03AlzModel{TCoder}"/>,
/// shared with <see cref="Uharc03AlzEncoder"/>.
/// </summary>
internal static class Uharc03AlzDecoder {

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes of a 0.3 ALZ stream.</summary>
  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize) {
    if (method is < UharcMethods.Alz03First or > UharcMethods.Alz03Last)
      throw new ArgumentOutOfRangeException(nameof(method));

    var output = new byte[unpackedSize];
    if (unpackedSize == 0)
      return output;

    var model = new Uharc03AlzModel<Uharc03Decoding>(new(new Uharc03RangeDecoder(packed, offset, length)), method);
    var position = 0;
    while (position < unpackedSize)
      position = model.Step(output, position, default);
    return output;
  }
}

/// <summary>The 0.3 ALZ model, written once for both directions (<see cref="IUharcCoder"/>).</summary>
/// <remarks>
/// Each step is a three-way decision: switch the literal model, copy, or literal. Literals come
/// from one of six context groups (plain bytes, or differences from the byte 1..4 back); copies
/// take a distance slot, a recent distance or a long run of the previous byte, and a length
/// class. Every output byte, copied or not, trains the current literal group. The stream never
/// resets its models.
/// </remarks>
internal sealed class Uharc03AlzModel<TCoder> where TCoder : struct, IUharcCoder {

  private const int Bottom = 0x4000;
  private const int DistanceSymbols = 61;
  private const int LongRunSymbol = 60;

  private static readonly int[] GroupContexts = [512, 32, 32, 96, 64, 512];
  private static readonly int[] RecentByFamily = [5, 8, 16];

  // Group 3: symbol -> one of 32 classes (unlike 0.4/0.6's table, no class is held back).
  private static readonly byte[] TripleClass = [
    0, 1, 2, 3, 4, 5, 6, 7, 7, 8, 8, 9, 9, 10, 10, 10, 11, 11, 11, 11, 11, 12, 12, 12, 12, 12, 12, 12, 12, 13, 13, 13,
    13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 15,
    15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
    15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
    16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
    17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17,
    17, 17, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 19, 19, 19, 19, 19, 19, 19, 19, 19,
    19, 19, 19, 19, 20, 20, 20, 20, 20, 20, 20, 20, 21, 21, 21, 21, 21, 22, 22, 22, 23, 23, 24, 24, 25, 25, 26, 27, 28, 29, 30, 31,
  ];

  // History length above which the next lower distance slot becomes codable.
  private static readonly uint[] SlotThreshold = [
    0xFFFFFFFF, 0x780000, 0x700000, 0x680000, 0x600000, 0x580000, 0x500000, 0x480000,
    0x400000, 0x3C0000, 0x380000, 0x340000, 0x300000, 0x2C0000, 0x280000, 0x240000,
    0x200000, 0x1E0000, 0x1C0000, 0x1A0000, 0x180000, 0x160000, 0x140000, 0x120000,
    0x100000, 0xE0000, 0xC0000, 0xA0000, 0x80000, 0x60000, 0x40000, 0x30000,
    0x20000, 0x18000, 0x10000, 0xC000, 0x8000, 0x6000, 0x4000, 0x3000,
    0x2000, 0x1800, 0x1000, 0xC00, 0x800, 0x600, 0x400, 0x300,
    0x200, 0x100, 0x80, 0x40, 0x20, 0x10, 0x8, 0x4, 0x2,
  ];

  // Distance slots 0..55, from the longest down: [base, base + span).
  private static readonly uint[] SlotBase = [
    0x780000, 0x700000, 0x680000, 0x600000, 0x580000, 0x500000, 0x480000, 0x400000,
    0x3C0000, 0x380000, 0x340000, 0x300000, 0x2C0000, 0x280000, 0x240000, 0x200000,
    0x1E0000, 0x1C0000, 0x1A0000, 0x180000, 0x160000, 0x140000, 0x120000, 0x100000,
    0xE0000, 0xC0000, 0xA0000, 0x80000, 0x60000, 0x40000, 0x30000, 0x20000,
    0x18000, 0x10000, 0xC000, 0x8000, 0x6000, 0x4000, 0x3000, 0x2000,
    0x1800, 0x1000, 0xC00, 0x800, 0x600, 0x400, 0x300, 0x200,
    0x100, 0x80, 0x40, 0x20, 0x10, 0x8, 0x4, 0x2,
  ];

  private static readonly uint[] SlotSpan = [
    0x80000, 0x80000, 0x80000, 0x80000, 0x80000, 0x80000, 0x80000, 0x80000,
    0x40000, 0x40000, 0x40000, 0x40000, 0x40000, 0x40000, 0x40000, 0x40000,
    0x20000, 0x20000, 0x20000, 0x20000, 0x20000, 0x20000, 0x20000, 0x20000,
    0x20000, 0x20000, 0x20000, 0x20000, 0x20000, 0x20000, 0x10000, 0x10000,
    0x8000, 0x8000, 0x4000, 0x4000, 0x2000, 0x2000, 0x1000, 0x1000,
    0x800, 0x800, 0x400, 0x400, 0x200, 0x200, 0x100, 0x100,
    0x100, 0x80, 0x40, 0x20, 0x10, 0x8, 0x4, 0x2,
  ];

  // Length classes: 0 is a run of 255 (lengths 255), 21 (repeats only) is 2.
  private static readonly byte[] LengthBase = [0xFF, 0xBF, 0x7F, 0x5F, 0x3F, 0x2F, 0x1F, 0x17, 0x13, 0x0F, 0x0D, 0x0C, 0x0B, 0x0A, 0x09, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02];
  private static readonly byte[] LengthExtra = [0, 0x40, 0x40, 0x20, 0x20, 0x10, 0x10, 0x08, 0x04, 0x04, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

  private static readonly ushort[] LengthSeed = [
    0x0000, 0x0010, 0x0018, 0x0028, 0x0038, 0x0058, 0x007C, 0x00CC, 0x0130, 0x018C, 0x0244, 0x02EF,
    0x0401, 0x04C9, 0x05CD, 0x071F, 0x08E1, 0x0B39, 0x0E59, 0x1241, 0x17B9, 0x2119, 0x2710,
  ];

  private static readonly ushort[] RecentSeed = [
    0x0000, 0x0190, 0x0352, 0x0546, 0x076C, 0x09C4, 0x0C4E, 0x0F0A, 0x11F8, 0x1518, 0x186A, 0x1BEE, 0x1FA4, 0x238C,
  ];

  private static readonly ushort[] LongRun = [
    0x0000, 0x0001, 0x0002, 0x0003, 0x0005, 0x0007, 0x0009, 0x000C, 0x0011, 0x0018, 0x0021, 0x002C, 0x0039, 0x0049, 0x0059, 0x0063,
  ];

  // How often to halve a group's contexts when its step changes: [group * 16 + old * 4 + new].
  private static readonly byte[] StepChangeHalvings = [
    0, 0, 1, 2, 2, 0, 0, 1, 4, 2, 0, 0, 6, 4, 2, 0,
    0, 0, 1, 2, 2, 0, 0, 1, 4, 2, 0, 0, 6, 4, 2, 0,
    0, 0, 1, 2, 2, 0, 0, 1, 4, 2, 0, 0, 6, 4, 2, 0,
    0, 0, 1, 2, 2, 0, 0, 1, 4, 2, 0, 0, 6, 4, 2, 0,
    0, 0, 1, 2, 2, 0, 0, 1, 4, 2, 0, 0, 6, 4, 2, 0,
    0, 0, 1, 2, 2, 0, 0, 1, 4, 2, 0, 0, 6, 4, 2, 0,
  ];

  private TCoder _coder;
  private int _switches;
  private int _switchValue;
  private readonly byte[] _window;
  private readonly uint _mask;
  private uint _position;
  private uint _historyLength = 31;
  private readonly uint _historyLimit;

  private readonly ushort[][] _frequency = new ushort[6][];
  private readonly int[][] _total = new int[6][];
  private int _group;
  private int _context = 0xFF;
  private int _lag;
  private int _step = 3;
  private bool _rawLiterals;
  private bool _highLiterals;
  private bool _positive = true;
  private uint _recentBytes = uint.MaxValue;
  private uint _phase;
  private uint _lastRun;

  private readonly ushort[] _steps;
  private readonly ushort[] _decisions = new ushort[32];
  private int _outcomes = 7;

  private readonly ushort[] _distance = new ushort[DistanceSymbols + 1];
  private int _slotLimit = 52;
  private readonly ushort[] _newLength = (ushort[])LengthSeed.Clone();
  private readonly ushort[] _repeatLength = (ushort[])LengthSeed.Clone();
  private readonly ushort[] _recentIndex = (ushort[])RecentSeed.Clone();
  private readonly ushort[] _nearIndex = [0x0000, 0x1194, 0x238C];
  private readonly uint[] _recent = new uint[16];
  private readonly int _recentCount;

  public Uharc03AlzModel(TCoder coder, int method) {
    var stepThree = method == 79 ? (ushort)0x40 : (ushort)0x20;
    if (method is 78 or 79)
      method = 36;

    var size = 1u << ((method - 36) % 14 + 10);
    this._window = new byte[size];
    this._mask = size - 1;
    this._historyLimit = size - 511;
    this._coder = coder;
    this._steps = [2, 8, stepThree, 128];
    this._recentCount = RecentByFamily[method <= 49 ? 0 : method <= 63 ? 1 : 2];
    Array.Fill(this._recent, uint.MaxValue);

    for (var g = 0; g < 6; ++g) {
      this._frequency[g] = new ushort[GroupContexts[g] * 256];
      this._total[g] = new int[GroupContexts[g]];
      this.Seed(g);
    }

    for (var s = 53; s <= 60; ++s)
      this._distance[s] = (ushort)(0x1FF + (s - 53) * 0x200);
    this._distance[61] = 0x1000;
    for (var h = 0; h < 8; ++h) {
      this._decisions[h * 4 + 1] = 1;
      this._decisions[h * 4 + 2] = 0xC00;
      this._decisions[h * 4 + 3] = 0x2000;
    }
  }

  /// <summary>Recent distances held, by method family (5, 8 or 16).</summary>
  public int RecentCount => this._recentCount;

  /// <summary>Distance <paramref name="index"/> of the recent list; <see cref="uint.MaxValue"/> when no copy has filled it.</summary>
  public uint Recent(int index) => this._recent[index];

  /// <summary>The farthest distance a copy may reach now.</summary>
  public uint Reach => Math.Min(this._historyLength + 1, (uint)this._window.Length);

  /// <summary>
  /// Whether a new distance can be coded now: its slot has a share of the distance table (slots
  /// open as the history grows) and its offset lies within the slot as narrowed by the history.
  /// </summary>
  public bool CanCodeDistance(uint distance) {
    if (distance == 0 || distance > this.Reach)
      return false;
    if (distance == 1)
      return true;

    var slot = SlotOf(distance);
    if (slot < 52 && this._historyLength < SlotThreshold[slot + 1])
      return false;
    var span = SlotSpan[slot];
    if (span + SlotBase[slot] - 1 > this._historyLength)
      span = this._historyLength - SlotBase[slot] + 1;
    return distance - SlotBase[slot] < span;
  }

  private static int SlotOf(uint distance) {
    var slot = 0;
    while (SlotBase[slot] > distance)
      ++slot;
    return slot;
  }

  /// <summary>The literal group the model codes in (0 plain bytes, 1..5 differences).</summary>
  public int Group => this._group;

  /// <summary>
  /// Codes a model switch (encoder): the switch decision, then <paramref name="value"/> — 0..5 a
  /// literal group, 6..9 a step — applied as the decoder applies it at <paramref name="position"/>.
  /// </summary>
  public void Select(int value, int position) {
    this._switchValue = value;
    if (this.DecodeDecision(0) != 0)
      throw new InvalidOperationException("UHARC: 0.3 ALZ switch decision lost.");
    this.Switch(position);
  }

  /// <summary>
  /// Codes one step — a copy (token 1 new distance, 3 a recent one by <see cref="UharcAlzChoice.Slot"/>)
  /// or a literal (token 4); model switches go through <see cref="Select"/>. Returns the position
  /// after the bytes it produced.
  /// </summary>
  public int Step(byte[] output, int position, in UharcAlzChoice choice) {
    while (true) {
      var decision = this.DecodeDecision(choice.Token == 4 ? 2 : 1);
      if (decision != 0)
        return decision == 1 ? this.DecodeCopy(output, position, choice) : this.DecodeLiteral(output, position, choice.Literal);
      if (++this._switches > 1 << 20)
        throw new InvalidDataException("UHARC: ALZ switch loop.");
      this.Switch(position);
    }
  }

  private void Seed(int group) {
    Array.Fill(this._frequency[group], (ushort)1);
    Array.Fill(this._total[group], 256);
  }

  // ── Decisions and model switches ───────────────────────────────────────

  /// <summary>0 switches the literal model, 1 is a copy, 2 a literal.</summary>
  private int DecodeDecision(int target) {
    var row = this._outcomes * 4;
    var count = this._coder.Count(this._decisions[row + 3], TCoder.Encoding ? this._decisions[row + target] : 0u);
    var decision = 2;
    while (decision > 0 && count < this._decisions[row + decision])
      --decision;
    this._coder.Consume(this._decisions[row + decision], (uint)(this._decisions[row + decision + 1] - this._decisions[row + decision]));
    if (decision == 0)
      return 0;

    if (decision == 1)
      this._decisions[row + 2] += 64;
    this._decisions[row + 3] += 64;
    if (this._decisions[row + 3] >= Bottom) {
      var literalStart = this._decisions[row + 2];
      var copyWidth = (literalStart - this._decisions[row + 1] + 1) >> 1;
      var literalWidth = (this._decisions[row + 3] - literalStart + 1) >> 1;
      var start = (ushort)(this._decisions[row + 1] + copyWidth);
      this._decisions[row + 2] = start;
      this._decisions[row + 3] = (ushort)(start + literalWidth);
    }

    this._outcomes = (decision + 2 * this._outcomes - 1) & 7;
    return decision;
  }

  private void Switch(int position) {
    var value = (int)this._coder.Uniform(12, TCoder.Encoding ? (uint)this._switchValue : 0u);    switch (value) {
      case <= 5:
        this._group = value;
        this._lag = (value - 1) & 3;
        this._step = 1;
        this._context = 0;
        if (position != 0 && value is 0 or 5)
          this.Seed(value);
        break;
      case <= 9: {
        var previous = this._step;
        this._step = value - 6;
        if (position == 0)
          break;
        var times = StepChangeHalvings[16 * this._group + 4 * previous + this._step];
        for (var c = 0; c < GroupContexts[this._group] && times != 0; ++c)
          for (var t = 0; t < times; ++t)
            this.Halve(c);
        break;
      }
      case 10:
        this._rawLiterals = !this._rawLiterals;
        break;
      default:
        this._highLiterals = !this._highLiterals;
        break;
    }
  }

  // ── Literal model ──────────────────────────────────────────────────────

  private int DecodeLiteral(byte[] output, int position, byte literal) {
    // The model symbol is the complemented byte, or in groups 1..5 its signed difference.
    byte target = 0;
    if (TCoder.Encoding) {
      var difference = literal;
      if (this._group != 0) {
        difference = (byte)(literal - this.LagByte());
        if (!this._positive)
          difference = (byte)-difference;
      }

      target = (byte)~difference;
    }

    int symbol;
    if (this._rawLiterals)
      symbol = (int)this._coder.Uniform(256, target);
    else
      symbol = this.DecodeSymbol(this._highLiterals ? 128 : 0, target);

    this.Learn(symbol);
    var value = (byte)~symbol;
    if (this._group != 0) {
      var difference = this._positive ? value : (byte)-value;
      if (value >= 0x80)
        this._positive = !this._positive;
      value = (byte)(difference + this.LagByte());
    }

    this._recentBytes = (this._recentBytes << 8) | value;
    this.Put(output, position, value);
    this._window[this._position] = value;
    this.Advance();
    return position + 1;
  }

  private byte LagByte() => (byte)(this._recentBytes >> (8 * this._lag));

  /// <summary>Decodes a symbol of the current context among <paramref name="first"/>..255; 255 lies nearest 0.</summary>
  private int DecodeSymbol(int first, int target) {
    var basis = this._context * 256;
    var counts = this._frequency[this._group];
    var total = 0u;
    var above = 0u;
    for (var s = first; s < 256; ++s) {
      total += counts[basis + s];
      if (s > target)
        above += counts[basis + s];
    }

    var wanted = this._coder.Count(total, TCoder.Encoding ? above : 0u);
    var low = 0u;
    for (var s = 255; s >= first; --s) {
      var f = (uint)counts[basis + s];
      if (wanted < low + f) {
        this._coder.Consume(low, f);
        return s;
      }

      low += f;
    }

    throw new InvalidDataException("UHARC: ALZ literal outside its context.");
  }

  /// <summary>Counts <paramref name="symbol"/> in the current context and moves to the next context.</summary>
  private void Learn(int symbol) {
    var counts = this._frequency[this._group];
    var step = this._steps[this._step];
    counts[this._context * 256 + symbol] += step;
    this._total[this._group][this._context] += step;
    if (this._total[this._group][this._context] >= Bottom)
      this.Halve(this._context);

    this._context = this._group switch {
      0 or 5 => ((this._context & 0xC0) == (symbol & 0xC0) ? 0x100 : 0) | symbol,
      1 => UharcAlzSideModel.PhaseOfClass[this._context & 0xF] * 16 + UharcAlzSideModel.DeltaClass[symbol],
      3 => (int)(this._phase % 3) * 32 + TripleClass[symbol],
      _ => (int)(this._lag & this._phase & 0xFF) * 16 + UharcAlzSideModel.DeltaClass[symbol],
    };
    ++this._phase;
  }

  private void Halve(int context) {
    var counts = this._frequency[this._group];
    var basis = context * 256;
    var total = 0;
    for (var s = 0; s < 256; ++s) {
      var f = (counts[basis + s] + 1) >> 1;
      counts[basis + s] = (ushort)f;
      total += f;
    }

    this._total[this._group][context] = total;
  }

  /// <summary>The model symbol a copied byte stands for: the complemented byte, or its signed difference.</summary>
  private int SymbolOf(byte value) {
    if (this._group == 0)
      return (byte)~value;

    var difference = (byte)(value - this.LagByte());
    if (!this._positive)
      difference = (byte)-difference;
    if (difference >= 0x80)
      this._positive = !this._positive;
    return (byte)~difference;
  }

  private void Put(byte[] output, int position, byte value) {
    if (position < output.Length)
      output[position] = value;
  }

  private void Advance() {
    if (this._historyLength < this._historyLimit)
      ++this._historyLength;
    this._position = (this._position + 1) & this._mask;
  }

  // ── Copies ─────────────────────────────────────────────────────────────

  private int DecodeCopy(byte[] output, int position, in UharcAlzChoice choice) {
    while (this._historyLength >= SlotThreshold[this._slotLimit]) {
      this.OpenSlot(this._slotLimit);
      --this._slotLimit;
    }

    var wantedSlot = !TCoder.Encoding ? 0
      : choice.Token == 1 ? choice.Distance == 1 ? 56 : SlotOf(choice.Distance)
      : choice.Slot == 0 ? 59 : choice.Slot < 3 ? 58 : 57;
    var slot = this.DecodeCumulative(this._distance, DistanceSymbols, wantedSlot);
    this.CountDistance(slot);
    if (slot == LongRunSymbol)
      return this.DecodeLongRun(output, position);

    // Slots 0..56 are new distances; 57, 58 and 59 pick a recent one.
    uint distance;
    int recent;
    var repeated = slot >= 57;
    if (!repeated) {
      distance = slot == 56 ? 1u : this.DecodeDistance(slot, choice.Distance);
      recent = this._recentCount - 1;
    } else {
      recent = slot switch {
        57 => 3 + (this._recentCount - 4) - this.DecodeCumulative(this._recentIndex, this._recentCount - 3, this._recentCount - 1 - choice.Slot),
        58 => 2 - this.DecodeCumulative(this._nearIndex, 2, 2 - choice.Slot),
        _ => 0,
      };
      if (recent >= this._recentCount)
        throw new InvalidDataException("UHARC: ALZ recent distance out of range.");
      distance = this._recent[recent];
      if (recent != 0)
        this.CountRecent(recent);
    }

    if (recent != 0 && distance > 1) {
      for (var j = recent; j > 0; --j)
        this._recent[j] = this._recent[j - 1];
      this._recent[0] = distance;
    }

    if (distance == 0 || distance > this._historyLength + 1 || distance > this._window.Length)
      throw new InvalidDataException("UHARC: ALZ copy reaches before the start of the stream.");

    var lengths = repeated ? this._repeatLength : this._newLength;
    var classes = repeated ? 22 : 21;
    var wantedClass = TCoder.Encoding ? LengthClassOf(choice.Length) : 0;
    var lengthClass = this.DecodeCumulative(lengths, classes, wantedClass);
    var extra = LengthExtra[lengthClass] != 0 ? this._coder.Uniform(LengthExtra[lengthClass], (uint)(choice.Length - LengthBase[wantedClass])) : 0;
    var count = (byte)(LengthBase[lengthClass] + extra);
    if (TCoder.Encoding && count != choice.Length)
      throw new InvalidOperationException("UHARC: 0.3 ALZ copy length cannot be coded.");
    CountLength(lengths, classes, lengthClass);
    if (count < 2)
      throw new InvalidDataException("UHARC: ALZ copy too short.");

    var source = (this._position - distance) & this._mask;
    for (var i = 0; i < count; ++i) {
      var value = this._window[source];
      source = (source + 1) & this._mask;
      this._window[this._position] = value;
      this.Advance();
      this.Learn(this.SymbolOf(value));
      this._recentBytes = (this._recentBytes << 8) | value;
      this.Put(output, position + i, value);
    }

    return position + count;
  }

  /// <summary>The length class of copy length <paramref name="length"/> (2..255).</summary>
  internal static int LengthClassOf(int length) {
    var lengthClass = 0;
    while (LengthBase[lengthClass] > length)
      ++lengthClass;
    return lengthClass;
  }

  private uint DecodeDistance(int slot, uint target) {
    var distance = SlotBase[slot];
    var offset = TCoder.Encoding ? target - distance : 0;
    var span = SlotSpan[slot];
    if (distance > this._historyLength + 1)
      throw new InvalidDataException("UHARC: ALZ distance slot beyond the decoded data.");
    if (span + distance - 1 > this._historyLength)
      span = this._historyLength - distance + 1;
    if (span >= 2) {
      if (span > 0x1000) {
        var blocks = ((span - 1) >> 12) + 1;
        var block = this._coder.Uniform(blocks, offset >> 12);
        distance += block << 12;
        span = block + 1 == blocks ? span - (block << 12) : 0x1000;
        offset -= block << 12;
      }

      if (span >= 2)
        distance += this._coder.Uniform(span, offset);
    }

    if (TCoder.Encoding && distance != target)
      throw new InvalidOperationException("UHARC: 0.3 ALZ distance cannot be coded here.");
    return distance;
  }

  /// <summary>Decodes a symbol 0..<paramref name="symbols"/>−1 of a cumulative table whose total is at index <paramref name="symbols"/>.</summary>
  private int DecodeCumulative(ushort[] table, int symbols, int target) {
    if (TCoder.Encoding && (target < 0 || target >= symbols || table[target + 1] == table[target]))
      throw new InvalidOperationException("UHARC: 0.3 ALZ symbol has no share of its table.");
    var count = this._coder.Count(table[symbols], TCoder.Encoding ? table[target] : 0u);
    var symbol = symbols - 1;
    while (symbol > 0 && count < table[symbol])
      --symbol;
    this._coder.Consume(table[symbol], (uint)(table[symbol + 1] - table[symbol]));
    return symbol;
  }

  private static void HalveCumulative(ushort[] table, int symbols, int floor) {
    for (var i = symbols; i > 0; --i) {
      var width = (table[i] - table[i - 1] + 1) >> 1;
      table[i] = (ushort)Math.Max(width, floor);
    }

    for (var i = 2; i <= symbols; ++i)
      table[i] += table[i - 1];
  }

  private void CountDistance(int slot) {
    if (slot != LongRunSymbol)
      for (var i = slot + 1; i < DistanceSymbols; ++i)
        this._distance[i] += 32;
    this._distance[DistanceSymbols] += (ushort)(slot == LongRunSymbol ? 4 : 32);
    if (this._distance[DistanceSymbols] >= Bottom)
      HalveCumulative(this._distance, DistanceSymbols, 0);
  }

  /// <summary>Gives distance slot <paramref name="slot"/> − 1 a share once the history is long enough.</summary>
  private void OpenSlot(int slot) {
    var share = (ushort)((this._distance[slot + 1] + 1) >> 1);
    for (var i = slot; i <= DistanceSymbols; ++i)
      this._distance[i] += share;
    HalveCumulative(this._distance, DistanceSymbols, 0);
  }

  private static void CountLength(ushort[] table, int symbols, int lengthClass) {
    for (var i = lengthClass + 1; i < symbols; ++i)
      table[i] += 64;
    table[symbols] += 64;
    if (table[symbols] >= Bottom)
      HalveCumulative(table, symbols, 0);
  }

  private void CountRecent(int recent) {
    if (recent < 3) {
      var t = this._nearIndex;
      if (recent == 2)
        t[1] += 32;
      t[2] += 32;
      if (t[2] >= Bottom) {
        var first = Math.Max((t[1] - t[0] + 1) >> 1, 0x400);
        var second = Math.Max((t[2] - t[1] + 1) >> 1, 0x400);
        t[1] = (ushort)(t[0] + first);
        t[2] = (ushort)(t[1] + second);
      }

      return;
    }

    var symbols = this._recentCount - 3;
    var table = this._recentIndex;
    for (var a = this._recentCount - recent; a < symbols; ++a)
      table[a] += 32;
    table[symbols] += 32;
    if (table[symbols] >= Bottom)
      HalveCumulative(table, symbols, 0x100);
  }

  // ── Long runs ──────────────────────────────────────────────────────────

  private int DecodeLongRun(byte[] output, int position) {
    var lengthClass = this.DecodeCumulative(LongRun, 15, 0);
    uint extra;
    if (lengthClass == 14)
      extra = this._lastRun;
    else {
      uint span = 0x80;
      extra = 0;
      if (lengthClass < 13) {
        span = 1u << (19 - lengthClass);
        extra = span;
      }

      if (span > 0x2000) {
        var blocks = span >> 12;
        extra += this._coder.Uniform(blocks, 0) << 12;
        span = 0x1000;
      }

      extra += this._coder.Uniform(span, 0);
      this._lastRun = extra;
    }

    var total = extra + 0x200;
    var value = this._window[(this._position - 1) & this._mask];
    for (var i = 0; i < 0x1FF; ++i) {
      this._window[this._position] = value;
      this.Advance();
    }

    // Five bytes go through the predictor; the rest of the run repeats their model symbol.
    var symbol = 0;
    for (var i = 0; i < 5; ++i) {
      symbol = this.SymbolOf(value);
      this._recentBytes = (this._recentBytes << 8) | value;
      this.Put(output, position + i, value);
      this.Learn(symbol);
    }

    for (var i = 5u; i < total; ++i) {
      this.Put(output, position + (int)i, value);
      this.Learn(symbol);
    }

    return position + (int)total;
  }
}
