namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.6 PPM decoder (stream methods 24..39, the <c>-mx</c> mode), implemented from
/// docs/UHARC-ON-DISK.md section "PPM".
/// </summary>
/// <remarks>
/// Every step first tries an LZP-style copy predicted from hashed contexts; if none is taken,
/// one literal goes through a cascade of models: an order-4 record, an order-2 record, a
/// binary-data ring predictor, and finally frequency contexts with SSE. The stream is one of
/// two kinds: text (with a byte-to-word transform at the output) or binary.
/// </remarks>
internal sealed partial class UharcPpmDecoder {

  private const uint NoPosition = uint.MaxValue;
  private const int HashSize = 0x40000;
  private const int Order4Records = 0x40000;
  private const int Order4Stride = 32;
  private const int Order2Records = 0x20000;
  private const int Order2Stride = 20;
  private const int MaxExcluded = 23;

  private readonly UharcRangeDecoder _coder;
  private readonly byte[] _window;
  private readonly uint _mask;
  private readonly uint _windowSize;
  private uint _position;

  /// <summary>0 = text stream, 1 = binary stream.</summary>
  private int _kind;
  private long _textEnd = long.MaxValue;

  /// <summary>Output position up to which the word transform applies (fixed by the stream header).</summary>
  private long _transformEnd;

  // Hashed copy prediction.
  private readonly uint[] _hashPosition = new uint[HashSize];
  private readonly uint[] _hashContext = new uint[HashSize];
  private readonly uint[] _deepNewest = new uint[HashSize];
  private readonly uint[] _deepPrevious = new uint[HashSize];
  private readonly byte[] _hashLength = new byte[HashSize];
  private readonly byte[] _hashConfidence = new byte[HashSize];
  private int _hashSlot;
  private uint _lastDistance = NoPosition;
  private uint _distanceStreak;
  private uint _runCount;
  private bool _hasExcluded;
  private uint _excludedPosition;
  private readonly ushort[,] _copyClass = new ushort[256, 4];
  private int _copyHistory;
  private readonly ushort[] _lengthProbability = new ushort[0x800];
  private readonly byte[] _copyBuffer = new byte[0x100];

  // Global hit statistics shared by the records and the copy stage.
  private uint _statTotal = 1;
  private uint _statHits = 1;
  private uint _statOrder4 = 1;

  // Rolling context: the last twelve bytes as seen by the model.
  private uint _roll0;
  private uint _roll1;
  private uint _roll2;
  private uint _symbols;

  private readonly UharcAlzSideModel _side = new();
  private int _selector;

  private UharcPpmDecoder(byte[] packed, int offset, int length, int method) {
    this._windowSize = (uint)UharcMethods.DictionarySize(method);
    this._window = new byte[this._windowSize + 0x100];
    this._mask = this._windowSize - 1;
    this._coder = new UharcRangeDecoder(packed, offset, length);

    Array.Fill(this._hashPosition, NoPosition);
    Array.Fill(this._hashContext, NoPosition);
    Array.Fill(this._deepNewest, NoPosition);
    Array.Fill(this._deepPrevious, NoPosition);

    this.InitializeContexts();
    this.InitializeRanks();
  }

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes of a PPM stream.</summary>
  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.PpmFirst or > UharcMethods.PpmLast)
      throw new ArgumentOutOfRangeException(nameof(method));

    var output = new byte[unpackedSize];
    if (unpackedSize == 0)
      return output;

    var decoder = new UharcPpmDecoder(packed, offset, length, method);
    decoder.Run(output, fileEnds);
    return output;
  }

  // ── Stream header and reset points ─────────────────────────────────────

  private void ReadStreamKind() {
    this._kind = (int)this._coder.DecodeUniform(2);
    if (this._kind != 0)
      return;

    // A text stream states how many output bytes the word transform covers, in 11-bit groups.
    long total = 0;
    var shift = 0;
    do {
      total += (long)this._coder.DecodeUniform(0x800) << shift;
      shift += 11;
    } while (this._coder.DecodeUniform(2) != 0 && shift < 64);

    this._textEnd = total & 0xFFFFFFFF;
    this._transformEnd = this._textEnd;
  }

  private void Run(byte[] output, IReadOnlyList<long> fileEnds) {
    this.ReadStreamKind();
    var reset = new ResetPoints(fileEnds, output.Length);
    reset.Advance(this);
    this.ResetBlock();
    if (this._kind != 0)
      this.ResetRing(full: true);

    var written = 0;
    while (written < output.Length) {
      if (written >= this._textEnd) {
        this.SwitchToBinary();
        this.ResetRing(full: true);
        reset.Next = written;
      }

      if (written >= reset.Next) {
        reset.Advance(this);
        this.ResetBlock();
        if (this._kind != 0)
          this.ResetRing(full: false);
      }

      written = this.Step(output, written);
    }
  }

  /// <summary>Model resets: like LZP's, with a span of 256 KiB (text) or 64 KiB (binary).</summary>
  private sealed class ResetPoints(IReadOnlyList<long> ends, long total) {
    private int _file;
    public long Next;

    public void Advance(UharcPpmDecoder decoder) {
      var span = 0x40000L >> (decoder._kind * 2);
      var target = total - this.Next > span ? this.Next + span : total;
      target = Math.Min(target, decoder._textEnd);
      while (this._file < ends.Count && ends[this._file] < target)
        ++this._file;
      this.Next = this._file < ends.Count ? ends[this._file] : total;
    }
  }

  // ── One step: a predicted copy, or a literal ───────────────────────────

  private int Step(byte[] output, int written) {
    var length = this.TryCopy(out var copyContext, out var copyClass);
    if (copyContext >= 0) {
      this.CountCopyClass(copyContext, copyClass);
      if (copyClass != 1) {
        ++this._distanceStreak;
        this._hashLength[HashOf(this._roll0)] = 0;
      }
    }

    if (length > 0) {
      for (var i = 0; i < length && written < output.Length; ++i)
        written = this.EmitCopied(output, written, this._copyBuffer[i], length - i <= 8);
      return written;
    }

    this._runCount = 0;
    return this.DecodeLiteral(output, written);
  }

  private static int HashOf(uint context) => (int)(((context >> 13) ^ context) & 0x3FFFF);

  private int DeepHashOf() => (int)(((((this._roll0 >> 15) ^ this._roll0) << 4)
    ^ (((this._roll1 >> 15) ^ this._roll1) << 6)
    ^ ((this._roll2 >> 15) ^ this._roll2)) & 0x3FFFF);

  /// <summary>Predicts and decodes a copy into <see cref="_copyBuffer"/>; returns its length, 0 for none.</summary>
  private int TryCopy(out int classContext, out int decodedClass) {
    classContext = -1;
    decodedClass = 0;
    var position = this._position;
    var hash = HashOf(this._roll0);
    var deep = this.DeepHashOf();
    this._hashSlot = hash;
    var confidence = this._hashConfidence[hash];

    // The longest agreement (up to 64 bytes) of the two deep-context candidates with the bytes
    // just written; the later candidate wins ties.
    uint best = 0, candidate = 0;
    var current = position;
    while (current < 0x40)
      current += this._windowSize;
    for (var depth = 0; depth < 2; ++depth) {
      var other = depth == 0 ? this._deepPrevious[deep] : this._deepNewest[deep];
      if (other == NoPosition || ((other - position) & this._mask) < 0x140)
        continue;

      var o = other < 0x40 ? other + this._windowSize : other;
      if (this._window[current - 0xC] != this._window[o - 0xC])
        continue;

      uint n = 0;
      uint a = current - 1, b = o - 1;
      while (n < 0x40 && this._window[a] == this._window[b]) {
        ++n;
        --a;
        --b;
      }

      if (n >= best) {
        best = n;
        candidate = other;
      }
    }

    var kind = 0;
    var inRun = this._runCount != 0;
    var repeated = false;
    if (inRun) {
      candidate = (position - this._lastDistance) & this._mask;
      kind = this._runCount > 2 ? 0x51 : 0x50;
      repeated = true;
    } else if (best >= (this._distanceStreak >= 0x10 ? 0xCu : 0x30u))
      kind = 0x15;
    else {
      candidate = (position - this._lastDistance) & this._mask;
      if (((candidate - position) & this._mask) >= 0x104 && this.ContextAgrees(candidate)) {
        kind = 0x0B;
        repeated = true;
      } else if (confidence < 0x40 || this._hashLength[hash] != 0) {
        if (this._roll0 != this._hashContext[hash])
          kind = candidate != NoPosition ? 0x1F : 0;
        else {
          candidate = this._hashPosition[hash];
          kind = candidate != NoPosition ? 1 : 0;
        }
      }
    }

    this._hashPosition[hash] = position;
    this._hashContext[hash] = this._roll0;
    this._deepPrevious[deep] = this._deepNewest[deep];
    this._deepNewest[deep] = position;

    if (kind == 0 || ((candidate - position) & this._mask) < 0x100)
      return 0;

    if (!inRun)
      kind = (kind + this._copyHistory + (this._hashLength[hash] == 0 ? 0x27 : -1)) & 0xFF;

    if (this._hasExcluded && this._window[this._excludedPosition] == this._window[candidate & this._mask]) {
      this.CountCopyClass(kind, 2);
      this._hashLength[hash] = 0;
      this._runCount = 0;
      return 0;
    }

    decodedClass = this.DecodeCopyClass(kind);
    classContext = kind;
    if (decodedClass != 1)
      return 0;

    var length = this.DecodeCopyLength(hash, repeated);
    var distance = (position - (candidate & this._mask)) & this._mask;
    if (distance == this._lastDistance)
      this._distanceStreak >>= 1;
    else {
      this._lastDistance = distance;
      this._distanceStreak += (uint)length;
    }

    this._hashLength[hash] = (byte)length;
    if (length == 0xFF) {
      this._hasExcluded = false;
      this._excludedPosition = 0;
      ++this._runCount;
    } else {
      this._excludedPosition = ((candidate & this._mask) + (uint)length) & this._mask;
      this._hasExcluded = true;
      this._runCount = 0;
    }

    // Bytes are read as the copy proceeds, so an overlapping copy repeats what it just produced.
    var bytes = this._copyBuffer;
    var source = candidate & this._mask;
    for (var i = 0; i < length; ++i) {
      var behind = (source - position) & this._mask;
      bytes[i] = behind < (uint)i ? bytes[behind] : this._window[source];
      source = (source + 1) & this._mask;
    }

    return length;
  }

  private bool ContextAgrees(uint candidate) {
    var b = (candidate - 4) & this._mask;
    var before = (uint)this._window[b + 3] | (uint)this._window[b + 2] << 8 | (uint)this._window[b + 1] << 16 | (uint)this._window[b] << 24;
    var now = this._roll0;
    return (before & 0xFFFF) == (now & 0xFFFF)
      || (before & 0xFFFF00FF) == (now & 0xFFFF00FF)
      || (before & 0xFFFFFF00) == (now & 0xFFFFFF00);
  }

  /// <summary>Hit (1), miss (2), or — escape — a new multimedia class followed by a retry.</summary>
  private int DecodeCopyClass(int context) {
    while (true) {
      var total = (uint)this._copyClass[context, 3];
      var count = this._coder.GetCount(total);
      var symbol = 2;
      while (symbol > 0 && count < this._copyClass[context, symbol])
        --symbol;
      this._coder.Consume(this._copyClass[context, symbol], (uint)(this._copyClass[context, symbol + 1] - this._copyClass[context, symbol]));
      if (symbol != 0)
        return symbol;

      this.SelectClass((int)this._coder.DecodeUniform(10));
    }
  }

  private void CountCopyClass(int context, int decodedClass) {
    if (decodedClass == 1)
      this._copyClass[context, 2] += 0x20;
    var total = (ushort)(this._copyClass[context, 3] + 0x20);
    this._copyClass[context, 3] = total;
    if (total >= 0x4000) {
      uint low = this._copyClass[context, 1];
      uint hit = this._copyClass[context, 2];
      var a = ((hit - low + 1) >> 1) + low;
      var b = ((uint)total - hit + 1) >> 1;
      this._copyClass[context, 2] = (ushort)a;
      this._copyClass[context, 3] = (ushort)(a + b);
    }

    this._copyHistory = UharcPpmTables.LzpHistoryNext[this._copyHistory * 2 + decodedClass];
  }

  private int DecodeCopyLength(int hash, bool repeated) {
    int floor, set;
    if (repeated) {
      floor = UharcPpmTables.LengthFloorInRun[(this._statOrder4 << 2) / this._statTotal];
      set = (this._runCount > 0 ? 1 : 0) + 3 + (this._runCount > 2 ? 1 : 0);
    } else {
      if (this._statHits > this._statTotal)
        floor = UharcPpmTables.LengthFloorByHits[(this._statHits << 2) / this._statTotal];
      else {
        var v = (UharcPpmTables.LengthFloorDefault[(this._statOrder4 << 2) / this._statTotal] - this._kind) & 0xFF;
        if (this._markerState != 0)
          v = (v + v) & 0xFF;
        floor = v;
      }

      var remembered = this._hashLength[hash];
      set = (remembered > 0xE ? 1 : 0) + (remembered > 0x3E ? 1 : 0);
    }

    var node = 0xE;
    int result;
    while (true) {
      result = (node + 1) & 0xFF;
      if (floor < result && this._coder.DecodeBit(ref this._lengthProbability[set * 0x100 + node], 4) == 0)
        result = node;
      var next = UharcPpmTables.LengthTree[node * 2 + (result > node ? 1 : 0)];
      if (next == 0)
        return result;
      node = next;
    }
  }

  // ── Emitting a byte ────────────────────────────────────────────────────

  private int EmitCopied(byte[] output, int written, byte value, bool nearEnd) {
    written = this.Emit(output, written, value);
    this.AfterSymbol(value, nearEnd);
    this.Store(value);
    this.Advance();
    return written;
  }

  private void Store(byte value) {
    this._window[this._position] = value;
    if (this._position < 0x100)
      this._window[this._position + this._windowSize] = value;
  }

  private void Advance() => this._position = (this._position + 1) & this._mask;

  private int Emit(byte[] output, int written, byte symbol)
    => this._transform.Emit(symbol, written, this._transformEnd, output);

  private readonly UharcPpmTextTransform _transform = new();

  /// <summary>Per-symbol upkeep shared by copies and literals.</summary>
  private void AfterSymbol(byte value, bool trackMarkers) {
    this.StoreRecentContexts(value);
    if (trackMarkers)
      this.TrackMarkers(value);
    this.TrackRepetition(value);
    this.AdvanceRoll(value);
    ++this._symbols;
    ++this._sinceMiss;
    if (this._selector != 0)
      this._side.Observe(value, this._roll0, this._symbols);
    this.ComputeRecentSlots();
  }

  private void AdvanceRoll(byte value) {
    if (this._kind == 0 && value is >= 4 and <= 6) {
      if ((this._roll0 & 0xFF) == 0xDF)
        return;
      value = 0xDF;
    }

    this._roll2 = (this._roll2 << 8) | (this._roll1 >> 24);
    this._roll1 = (this._roll1 << 8) | (this._roll0 >> 24);
    this._roll0 = (this._roll0 << 8) | value;
  }

  /// <summary>The class predictor's context, in PPM numbering (ALZ's less 0xF8).</summary>
  private int ClassContext => this._selector == 0 ? 0x201 : this._side.Context - 0xF8;

  private void SelectClass(int selector) {
    if (selector > 9)
      throw new InvalidDataException("UHARC: PPM class out of range.");
    if (selector == this._selector)
      return;

    this._selector = selector;
    this._side.Select(selector);
  }
}
